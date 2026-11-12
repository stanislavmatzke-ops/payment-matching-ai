using System.Diagnostics;
using PaymentMatching.AiGateway;

namespace PaymentMatching.Matching;

/// <summary>Result of one run. <see cref="Reused"/> counts payments decided in an earlier run (idempotency).</summary>
public sealed record RunReport(IReadOnlyList<AuditRecord> Records, int Reused)
{
    public IReadOnlyList<UsageRecord> Calls => Records.SelectMany(r => r.Calls).ToList();
}

/// <summary>
/// Strangler fig: layer 1 (ported rules) decides first and claims its invoices; layer 2 (candidates
/// selected by code → AI → validation by code) only sees what the rules could not decide and never
/// overrides a rule. Payments are handled in statement order, so earlier matches claim invoices first.
/// </summary>
public sealed class MatchingPipeline(
    MatchingDataset data,
    AiGateway.AiGateway? gateway,
    IDecisionStore log,
    MatchingOptions options,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly CandidateSelector _selector = new(data, options);
    private readonly PromptBuilder _prompts = new(data);
    private readonly AiDecisionMaker _decisions = new(options);
    private readonly ProposalValidator _validator = new(data, new CandidateSelector(data, options), options);

    public async Task<RunReport> RunAsync(CancellationToken cancellationToken = default)
    {
        using var run = Telemetry.Source.StartActivity("matching.run");
        var rules = RulesLayer.Run(data);
        var policy = new RuleOutcomePolicy(data, options);
        var records = new List<AuditRecord>();
        var forAi = new List<BankPayment>();
        var reused = 0;

        // Layer 1: rules.
        foreach (var payment in data.Payments)
        {
            var key = DecisionLog.KeyFor(payment);
            if (log.TryGet(key, out var earlier))
            {
                records.Add(earlier);
                reused++;
                continue;
            }

            var rule = rules[payment.Id];
            var step = policy.Apply(payment, rule);
            if (step.Next == NextStep.AskAi)
            {
                forAi.Add(payment);
                continue;
            }

            using var span = Telemetry.Source.StartActivity("matching.payment");

            var decision = step.Decision!;
            if (decision.Outcome == Outcome.AutoMatched && decision.InvoiceNumbers.Any(log.MatchedInvoices.Contains))
            {
                decision = decision with { Outcome = Outcome.HumanReview, Reason = "Invoice is already matched to another payment." };
            }

            records.Add(Append(Traced(span, new AuditRecord(key, payment.Id, _time.GetUtcNow(), AuditRecord.RuleLayer, rule.Rule, null, decision, [], [], []))));
        }

        // Layer 2: AI over what the rules left, in statement order.
        if (gateway is null && options.DeferAiWithoutGateway)
        {
            forAi.Clear();
        }

        foreach (var payment in forAi)
        {
            using var span = Telemetry.Source.StartActivity("matching.payment");
            if (await DecideWithAiAsync(payment, rules[payment.Id].Rule, cancellationToken) is { } record)
            {
                records.Add(Append(Traced(span, record)));
            }
            else
            {
                span?.SetTag(Tags.PaymentId, payment.Id).SetTag(Tags.Deferred, true);
            }
        }

        run?.SetTag(Tags.Payments, data.Payments.Count)
            .SetTag(Tags.Reused, reused)
            .SetTag(Tags.ForAi, forAi.Count)
            .SetTag(Tags.Decided, records.Count - reused);

        var order = data.Payments.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return new RunReport(records.OrderBy(r => order[r.PaymentId]).ToList(), reused);
    }

    /// <summary>Null when the payment is left undecided for a later run (<see cref="MatchingOptions.DeferAiWhenBudgetExhausted"/>).</summary>
    private async Task<AuditRecord?> DecideWithAiAsync(BankPayment payment, string ruleCode, CancellationToken cancellationToken)
    {
        var key = DecisionLog.KeyFor(payment);
        var unavailable = log.MatchedInvoices;
        var candidates = _selector.Select(payment, unavailable);
        var candidateNumbers = candidates.Invoices.Select(c => c.Invoice.Number).ToList();
        var injection = InjectionDetector.LooksLikeInjection($"{payment.Message} {payment.CounterpartyName} {payment.VariableSymbol}");
        var payerKnown = data.CustomerOfPayer(payment) is not null;

        AuditRecord Record(Decision decision, string layer, IReadOnlyList<string> failures, IReadOnlyList<UsageRecord> calls) =>
            new(key, payment.Id, _time.GetUtcNow(), layer, ruleCode, layer == AuditRecord.AiLayer ? PromptBuilder.Version : null, decision, candidateNumbers, failures, calls);

        if (candidates.IsEmpty || gateway is null)
        {
            // Nothing to choose from (or AI switched off): no model call, no cost.
            var flags = injection ? new[] { Flags.PromptInjection } : [];
            var decision = injection ? new Decision(payment.Id, Outcome.NoMatch, [], DecidedBy.Policy, "Bank data contains instructions for an AI; not matched.", flags)
                : candidates.IsEmpty && !payerKnown ? new Decision(payment.Id, Outcome.NoMatch, [], DecidedBy.Policy, "No related invoice.", flags)
                : new Decision(payment.Id, Outcome.HumanReview, [], DecidedBy.Policy, candidates.IsEmpty ? "Paid by a known customer, but no open invoice is related." : "AI is switched off.", flags);
            return Record(decision, AuditRecord.PolicyLayer, [], []);
        }

        // Escalation costs ~30x a small-model call; skip it when no answer could lead to an automatic match.
        var escalate = !options.EscalateOnlyWhenAutoMatchPossible || (!injection && _validator.AnyValidProposal(payment, candidates, unavailable));
        var result = await gateway.CompleteAsync(
            _prompts.SystemPrompt,
            _prompts.UserContent(payment, candidates),
            AiMatchAnswer.Schema,
            AiMatchAnswer.Parse,
            a => !escalate || (a.Decision != AiMatchAnswer.Uncertain && a.Confidence >= options.EscalationConfidence),
            cancellationToken);

        if (result.BudgetExhausted && result.Calls.Count == 0 && options.DeferAiWhenBudgetExhausted)
        {
            // No model was asked: the payment waits for the next run instead of going to a person.
            return null;
        }

        var validation = result.Value is { Decision: AiMatchAnswer.Match or AiMatchAnswer.Uncertain } answer
            ? _validator.Validate(payment, answer.InvoiceNumbers, candidates, unavailable, injection)
            : null;
        var aiDecision = _decisions.Decide(payment, result, validation, payerKnown, injection);
        return Record(aiDecision, AuditRecord.AiLayer, validation?.Failures ?? [], result.Calls);
    }

    /// <summary>Tag names of the pipeline spans: synthetic ids, codes and counts only (ADR 0015).</summary>
    public static class Tags
    {
        public const string PaymentId = "payments.payment.id";
        public const string Layer = "payments.layer";
        public const string Rule = "payments.rule";
        public const string Outcome = "payments.outcome";
        public const string DecidedBy = "payments.decided_by";
        public const string Flags = "payments.flags";
        public const string Failures = "payments.failures";
        public const string Candidates = "payments.candidates";
        public const string Deferred = "payments.deferred";
        public const string Payments = "payments.count";
        public const string Reused = "payments.reused";
        public const string ForAi = "payments.for_ai";
        public const string Decided = "payments.decided";
    }

    private static AuditRecord Traced(Activity? span, AuditRecord record)
    {
        var decision = record.Decision;
        var outcome = decision.Outcome.ToString();
        span?.SetTag(Tags.PaymentId, record.PaymentId)
            .SetTag(Tags.Layer, record.Layer)
            .SetTag(Tags.Rule, record.RuleCode)
            .SetTag(Tags.Outcome, outcome)
            .SetTag(Tags.DecidedBy, decision.DecidedBy)
            .SetTag(Tags.Flags, decision.Flags.Count == 0 ? null : decision.Flags.ToArray())
            .SetTag(Tags.Failures, record.ValidationFailures.Count == 0 ? null : record.ValidationFailures.ToArray())
            .SetTag(Tags.Candidates, record.Candidates.Count);
        Telemetry.MatchingDecisions.Add(1, new(Tags.Layer, record.Layer), new(Tags.Outcome, outcome), new(Tags.DecidedBy, decision.DecidedBy));
        return record;
    }

    private AuditRecord Append(AuditRecord record)
    {
        log.Append(record);
        return record;
    }
}
