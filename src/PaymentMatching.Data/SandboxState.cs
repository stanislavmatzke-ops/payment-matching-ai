using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.Matching;

namespace PaymentMatching.Data;

/// <summary>What a person did with a payment in the app.</summary>
public static class ReviewAction
{
    public const string Confirmed = "confirmed";
    public const string Rejected = "rejected";
    public const string MatchedManually = "matched_manually";

    /// <summary>A person undid an automatic AI match: its invoices were freed and others (or none) chosen.</summary>
    public const string UndoneAi = "undone_ai";
}

/// <summary>Where a payment stands for the person, combining the pipeline's decision and the person's (SPEC 9b).</summary>
public static class PaymentState
{
    /// <summary>Matched automatically, or by a person (confirmed proposal or manual match).</summary>
    public const string Matched = "matched";

    /// <summary>Waits for a person: the pipeline sent it to review, or an agent proposed a match for it.</summary>
    public const string NeedsReview = "needs_review";

    /// <summary>Not matched on purpose by the pipeline (bank fee, unrelated payment, captured prompt injection).</summary>
    public const string NotMatched = "not_matched";

    /// <summary>A person decided not to match it.</summary>
    public const string Rejected = "rejected";

    /// <summary>Needs the AI layer; the scheduled job has not processed it yet.</summary>
    public const string WaitingForAi = "waiting_for_ai";

    public static IReadOnlyList<string> All { get; } = [Matched, NeedsReview, NotMatched, Rejected, WaitingForAi];
}

/// <summary>A proposal as it was shown to the person when they decided (feeds "live precision").</summary>
public sealed record ProposalSnapshot(string Id, IReadOnlyList<string> InvoiceNumbers, IReadOnlyList<string> ProposedBy);

/// <summary>
/// A person's decision, kept as the audit record of a <see cref="ReviewRow"/>. <see cref="Checks"/> are
/// the validation failures at the time; <see cref="Acknowledged"/> the warnings the person accepted.
/// </summary>
public sealed record HumanDecision(
    string PaymentId,
    string Action,
    IReadOnlyList<string> InvoiceNumbers,
    string? ProposalId,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Acknowledged,
    IReadOnlyList<ProposalSnapshot> Proposals,
    Guid UserId,
    DateTimeOffset At)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string Serialize(HumanDecision decision) => JsonSerializer.Serialize(decision, Json);

    public static HumanDecision Deserialize(string json) =>
        JsonSerializer.Deserialize<HumanDecision>(json, Json) ?? throw new InvalidDataException("Bad review JSON.");
}

/// <summary>Who proposed a match and why. An agent's reason is untrusted text (it may quote the payer).</summary>
public sealed record ProposalSource(string ProposedBy, string Reason, double? Confidence, string? PromptVersion, DateTimeOffset At, bool ByAgent);

/// <summary>
/// One proposed set of invoices for a payment, from the pipeline and/or agents (same invoices = same
/// id, <see cref="Proposal.IdFor"/>). <see cref="Checks"/> are recomputed now, so an invoice matched in
/// the meantime shows up as a failure.
/// </summary>
public sealed record QueueProposal(string Id, IReadOnlyList<string> InvoiceNumbers, IReadOnlyList<ProposalSource> Sources, IReadOnlyList<string> Checks);

/// <summary>
/// The current tenant's sandbox, read once per request through the tenant-filtered context: data,
/// the pipeline's decisions, agent proposals, a person's decisions and the matched invoices.
/// </summary>
public sealed class SandboxState
{
    private readonly Dictionary<string, BankPayment> _paymentById;
    private readonly Dictionary<string, AuditRecord> _decisionByPayment;
    private readonly Dictionary<string, List<Proposal>> _agentProposals;
    private readonly Dictionary<string, HumanDecision> _reviewByPayment;
    private readonly ProposalChecks _checks;

    private SandboxState(
        MatchingDataset data,
        Dictionary<string, AuditRecord> decisions,
        Dictionary<string, List<Proposal>> agentProposals,
        Dictionary<string, HumanDecision> reviews,
        Dictionary<string, string> matchedInvoices,
        MatchingOptions options)
    {
        Data = data;
        _paymentById = data.Payments.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _decisionByPayment = decisions;
        _agentProposals = agentProposals;
        _reviewByPayment = reviews;
        MatchedInvoices = matchedInvoices;
        _checks = new ProposalChecks(data, options);
    }

    public MatchingDataset Data { get; }

    /// <summary>Invoice number → payment it is matched to (automatically or by a person).</summary>
    public IReadOnlyDictionary<string, string> MatchedInvoices { get; }

    public IEnumerable<AuditRecord> Decisions => _decisionByPayment.Values;

    public IEnumerable<HumanDecision> Reviews => _reviewByPayment.Values;

    public static async Task<SandboxState> LoadAsync(PaymentsDbContext db, MatchingOptions? options = null, CancellationToken cancellationToken = default)
    {
        var data = await TenantData.LoadDatasetAsync(db, cancellationToken);

        // A decision belongs to the exact statement line (idempotency key), not just to a payment id.
        var keyToPayment = data.Payments.ToDictionary(DecisionLog.KeyFor, p => p.Id, StringComparer.Ordinal);
        var decisions = (await db.Decisions.AsNoTracking().Select(d => new { d.IdempotencyKey, d.Record }).ToListAsync(cancellationToken))
            .Where(r => keyToPayment.ContainsKey(r.IdempotencyKey))
            .ToDictionary(r => keyToPayment[r.IdempotencyKey], r => DecisionLog.Deserialize(r.Record), StringComparer.Ordinal);
        var reviews = (await db.Reviews.AsNoTracking().Select(r => new { r.DecisionKey, r.Record }).ToListAsync(cancellationToken))
            .Where(r => keyToPayment.ContainsKey(r.DecisionKey))
            .ToDictionary(r => keyToPayment[r.DecisionKey], r => HumanDecision.Deserialize(r.Record), StringComparer.Ordinal);
        var proposals = (await db.Proposals.AsNoTracking().Select(p => p.Record).ToListAsync(cancellationToken))
            .Select(Proposal.Deserialize)
            .GroupBy(p => p.PaymentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.At).ToList(), StringComparer.Ordinal);
        var matched = await db.InvoiceMatches.AsNoTracking().ToDictionaryAsync(m => m.InvoiceNumber, m => m.PaymentId, StringComparer.Ordinal, cancellationToken);
        return new SandboxState(data, decisions, proposals, reviews, matched, options ?? MatchingOptions.Default);
    }

    public BankPayment? Payment(string id) => _paymentById.GetValueOrDefault(id);

    public AuditRecord? Decision(string paymentId) => _decisionByPayment.GetValueOrDefault(paymentId);

    public HumanDecision? Review(string paymentId) => _reviewByPayment.GetValueOrDefault(paymentId);

    public IReadOnlyList<Proposal> AgentProposals(string paymentId) => _agentProposals.GetValueOrDefault(paymentId) ?? [];

    public string StateOf(string paymentId)
    {
        if (Review(paymentId) is { } review)
        {
            return review.Action == ReviewAction.Rejected || review.InvoiceNumbers.Count == 0 ? PaymentState.Rejected : PaymentState.Matched;
        }

        return Decision(paymentId)?.Decision.Outcome switch
        {
            Outcome.AutoMatched => PaymentState.Matched,
            Outcome.HumanReview => PaymentState.NeedsReview,
            Outcome.NoMatch => AgentProposals(paymentId).Count > 0 ? PaymentState.NeedsReview : PaymentState.NotMatched,
            _ => PaymentState.WaitingForAi,
        };
    }

    /// <summary>
    /// A person may act on a payment the pipeline decided but did not match, as long as no person has
    /// decided it yet. Payments waiting for the AI run and automatic matches are not open to actions.
    /// </summary>
    public bool IsActionable(string paymentId) =>
        Review(paymentId) is null && Decision(paymentId)?.Decision.Outcome is Outcome.HumanReview or Outcome.NoMatch;

    /// <summary>
    /// A person may undo a match the AI layer made on its own (ADR 0018), once: the payment's invoices
    /// are freed and the person picks others or none. Rule matches are not undone here.
    /// </summary>
    public bool CanUndoAi(string paymentId) =>
        Review(paymentId) is null
        && Decision(paymentId) is { Decision.Outcome: Outcome.AutoMatched } record
        && DecidedBy.IsAi(record.Decision.DecidedBy);

    public bool InjectionSuspected(BankPayment payment) => ProposalChecks.InjectionSuspected(payment, Decision(payment.Id));

    /// <summary>The pipeline's proposal (if any) and the agents' proposals, merged by invoice set.</summary>
    public IReadOnlyList<QueueProposal> ProposalsFor(BankPayment payment)
    {
        var sources = new List<(IReadOnlyList<string> Invoices, ProposalSource Source)>();
        if (Decision(payment.Id) is { Decision.Outcome: Outcome.HumanReview } record && record.Decision.InvoiceNumbers.Count > 0)
        {
            sources.Add((record.Decision.InvoiceNumbers, new ProposalSource(record.Decision.DecidedBy, record.Decision.Reason, record.Decision.Confidence, record.PromptVersion, record.At, false)));
        }

        sources.AddRange(AgentProposals(payment.Id).Select(p => (p.InvoiceNumbers, new ProposalSource(p.ProposedBy, p.Reason, null, null, p.At, true))));
        return sources
            .GroupBy(s => Proposal.IdFor(payment.Id, s.Invoices), StringComparer.Ordinal)
            .Select(g =>
            {
                var invoices = g.First().Invoices;
                return new QueueProposal(g.Key, invoices, g.Select(s => s.Source).ToList(), Check(payment, invoices));
            })
            .ToList();
    }

    /// <summary>The same checks as for an agent's proposal (<see cref="ProposalChecks"/>), against today's matched invoices.</summary>
    /// <param name="freeing">A payment whose own matches are being undone: its invoices count as open.</param>
    public IReadOnlyList<string> Check(BankPayment payment, IReadOnlyList<string> invoiceNumbers, string? freeing = null) =>
        _checks.Check(payment, invoiceNumbers, MatchedInvoices.Where(m => m.Value != freeing).Select(m => m.Key).ToHashSet(StringComparer.Ordinal), InjectionSuspected(payment));

    /// <summary>Invoices a person can pick for a manual match: not matched to any payment yet (or only to <paramref name="freeing"/>).</summary>
    public IReadOnlyList<InvoiceRecord> OpenInvoices(string? freeing = null) =>
        Data.Invoices.Where(i => !MatchedInvoices.TryGetValue(i.Number, out var by) || by == freeing).OrderBy(i => i.Number, StringComparer.Ordinal).ToList();
}
