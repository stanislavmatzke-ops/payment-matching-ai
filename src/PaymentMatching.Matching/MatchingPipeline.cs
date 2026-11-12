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

            var decision = step.Decision!;
            if (decision.Outcome == Outcome.AutoMatched && decision.InvoiceNumbers.Any(log.MatchedInvoices.Contains))
            {
                decision = decision with { Outcome = Outcome.HumanReview, Reason = "Invoice is already matched to another payment." };
            }

            records.Add(Append(new AuditRecord(key, payment.Id, _time.GetUtcNow(), AuditRecord.RuleLayer, rule.Rule, null, decision, [], [], [])));
        }

        // Layer 2: AI over what the rules left, in statement order.
        if (gateway is null && options.DeferAiWithoutGateway)
        {
            forAi.Clear();
        }

        foreach (var payment in forAi)
        {
            records.Add(Append(await DecideWithAiAsync(payment, rules[payment.Id].Rule, cancellationToken)));
        }

        var order = data.Payments.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return new RunReport(records.OrderBy(r => order[r.PaymentId]).ToList(), reused);
    }

    private async Task<AuditRecord> DecideWithAiAsync(BankPayment payment, string ruleCode, CancellationToken cancellationToken)
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

        var validation = result.Value is { Decision: AiMatchAnswer.Match or AiMatchAnswer.Uncertain } answer
            ? _validator.Validate(payment, answer.InvoiceNumbers, candidates, unavailable, injection)
            : null;
        var aiDecision = _decisions.Decide(payment, result, validation, payerKnown, injection);
        return Record(aiDecision, AuditRecord.AiLayer, validation?.Failures ?? [], result.Calls);
    }

    private AuditRecord Append(AuditRecord record)
    {
        log.Append(record);
        return record;
    }
}
