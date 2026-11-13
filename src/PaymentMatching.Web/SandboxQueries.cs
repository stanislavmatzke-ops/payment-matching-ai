using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Matching;
using static PaymentMatching.Web.Text;

namespace PaymentMatching.Web;

public sealed record SandboxCounts(int Customers, int Invoices, int Payments, int AutoMatched, int HumanReview, int NoMatch, int WaitingForAi);

/// <param name="AccountId">
/// The signed-in account as <c>provider:subject</c>, the form <c>Admin:Accounts</c> expects; shown only to its owner.
/// </param>
public sealed record MeView(string Name, string Email, string Provider, string AccountId, Guid TenantId, DateTimeOffset SandboxCreatedAt, SandboxCounts Counts);

/// <summary>
/// One payment. Bank statement text (<see cref="CounterpartyName"/>, <see cref="VariableSymbol"/>,
/// <see cref="Message"/>) is untrusted (written by the payer); Razor and JSON encode it on output.
/// <see cref="Status"/> is the pipeline's view, <see cref="State"/> includes the person's decision.
/// </summary>
public sealed record PaymentListItem(
    string Id,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    string CounterpartyName,
    string VariableSymbol,
    string Message,
    string Status,
    string? DecidedBy,
    IReadOnlyList<string> Flags,
    string State,
    bool InjectionSuspected);

public sealed record PaymentDecisionView(
    string Status,
    string DecidedBy,
    string Layer,
    string? RuleCode,
    string? PromptVersion,
    double? Confidence,
    IReadOnlyList<string> InvoiceNumbers,
    string Reason,
    IReadOnlyList<string> ValidationFailures);

public sealed record ReviewView(string Action, IReadOnlyList<string> InvoiceNumbers, string? ProposalId, string DecidedBy, DateTimeOffset At, IReadOnlyList<string> Checks, IReadOnlyList<string> Acknowledged);

/// <summary>One entry of a payment's audit trail. Texts quoted from a model or an agent are untrusted.</summary>
/// <param name="Icon">The icon of a model call: "ai", or "cache" for an answer reused from the cache.</param>
public sealed record TimelineEvent(DateTimeOffset At, string Kind, string Actor, string Summary, IReadOnlyList<string> InvoiceNumbers, IReadOnlyList<string> Details, string Icon = "");

public static class TimelineKind
{
    public const string Received = "received";
    public const string Decided = "decided";
    public const string ModelCall = "model_call";
    public const string Proposed = "proposed";
    public const string Reviewed = "reviewed";
}

public sealed record PaymentDetailView(
    PaymentListItem Payment,
    PaymentDecisionView? Decision,
    IReadOnlyList<Proposal> Proposals,
    ReviewView? Review,
    bool Actionable,
    IReadOnlyList<QueueProposal> Queue,
    IReadOnlyList<TimelineEvent> Timeline,
    IReadOnlyList<InvoiceFacts> Invoices,
    IReadOnlyList<InvoiceComparison> Comparisons)
{
    /// <summary>The AI matched this payment on its own and no person decided it yet: the person may undo it.</summary>
    public bool CanUndoAi { get; init; }
}

/// <summary>An invoice that a decision, review or proposal names, with what a person compares against the payment.</summary>
/// <param name="MatchedToPayment">The payment it is matched to in this sandbox; null while open.</param>
public sealed record InvoiceFacts(
    string Number, string Kind, string VariableSymbol, string CustomerId, string CustomerName, DateOnly DueDate, decimal Amount, string Currency, string? MatchedToPayment);

/// <summary>A named set of invoices against the payment: payment minus their sum (null when the currencies differ).</summary>
public sealed record InvoiceComparison(IReadOnlyList<string> InvoiceNumbers, decimal Total, string Currency, decimal? Difference);

public sealed record InvoiceOption(string Number, string VariableSymbol, string CustomerId, string CustomerName, DateOnly DueDate, decimal Amount, string Currency);

public sealed record DailyCost(DateOnly Day, int Calls, decimal CostUsd);

/// <summary>Of the proposals a person decided, how many they took (feedback from use, not ground truth).</summary>
public sealed record LivePrecision(string Source, int Decided, int Accepted)
{
    public double? Rate => Decided == 0 ? null : (double)Accepted / Decided;
}

public sealed record DashboardView(
    int Payments,
    int Processed,
    int AutoMatched,
    int NeedsReview,
    int NotMatched,
    int Rejected,
    int WaitingForAi,
    int HumanInterventions,
    int InjectionsCaught,
    int AiCalls,
    decimal AiCostUsd,
    IReadOnlyList<DailyCost> CostByDay,
    IReadOnlyList<LivePrecision> LivePrecision)
{
    /// <summary>Answers reused from the response cache: no model call, no cost (ADR 0013).</summary>
    public int CachedAnswers { get; init; }

    /// <summary>Payments waiting for a person because the app-wide AI budget was used up.</summary>
    public int AiBudgetUsedUp { get; init; }

    /// <summary>Payments the AI layer decided (only those the rules could not settle reach it).</summary>
    public int AiDecided { get; init; }

    /// <summary>Of <see cref="AiDecided"/>: matched on its own, after code checked the answer.</summary>
    public int AiAutoMatched { get; init; }

    /// <summary>Of <see cref="AiDecided"/>: sent to a person with a proposal.</summary>
    public int AiProposed { get; init; }

    /// <summary>AI matches a person undid (the feedback behind <see cref="AiPrecisionSeenByPeople"/>).</summary>
    public int AiUndone { get; init; }

    /// <summary>Share of the AI's own matches nobody undid; null before the first one.</summary>
    public double? AiPrecisionSeenByPeople => AiAutoMatched == 0 ? null : (double)(AiAutoMatched - AiUndone) / AiAutoMatched;

    public double? AutomationRate => Processed == 0 ? null : (double)AutoMatched / Processed;

    public decimal? AiCostPer1000Payments => Processed == 0 ? null : AiCostUsd / Processed * 1000;
}

/// <summary>
/// Read-only views of the signed-in person's sandbox. Every query runs through the tenant-filtered
/// <see cref="PaymentsDbContext"/>; nothing here names a tenant.
/// </summary>
public static class SandboxQueries
{
    public static async Task<MeView> MeAsync(PaymentsDbContext db, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var outcomes = await db.Decisions.GroupBy(d => d.Outcome).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        int Count(Outcome outcome) => outcomes.SingleOrDefault(o => o.Key == outcome.ToString())?.Count ?? 0;
        var payments = await db.Payments.CountAsync(cancellationToken);
        var counts = new SandboxCounts(
            await db.Customers.CountAsync(cancellationToken),
            await db.Invoices.CountAsync(cancellationToken),
            payments,
            Count(Outcome.AutoMatched),
            Count(Outcome.HumanReview),
            Count(Outcome.NoMatch),
            payments - outcomes.Sum(o => o.Count));
        return new MeView(user.Name, user.Email, user.Provider, $"{user.Provider}:{user.Subject}", user.TenantId, tenant.CreatedAt, counts);
    }

    public static async Task<IReadOnlyList<PaymentListItem>> PaymentsAsync(PaymentsDbContext db, CancellationToken cancellationToken)
    {
        var state = await SandboxState.LoadAsync(db, null, cancellationToken);
        return Items(state);
    }

    public static IReadOnlyList<PaymentListItem> Items(SandboxState state) => state.Data.Payments.Select(p => Item(state, p)).ToList();

    public static async Task<PaymentDetailView?> PaymentAsync(PaymentsDbContext db, string id, CancellationToken cancellationToken)
    {
        var state = await SandboxState.LoadAsync(db, null, cancellationToken);
        return Detail(state, id, await db.Users.AsNoTracking().Select(u => u.Name).SingleAsync(cancellationToken));
    }

    public static PaymentDetailView? Detail(SandboxState state, string id, string personName)
    {
        var payment = state.Payment(id);
        if (payment is null)
        {
            return null;
        }

        var record = state.Decision(id);
        var decision = record is null ? null : new PaymentDecisionView(
            PaymentStatus.Of(record),
            record.Decision.DecidedBy,
            record.Layer,
            record.RuleCode,
            record.PromptVersion,
            record.Decision.Confidence,
            record.Decision.InvoiceNumbers,
            record.Decision.Reason,
            record.ValidationFailures);
        var review = state.Review(id) is { } r
            ? new ReviewView(r.Action, r.InvoiceNumbers, r.ProposalId, personName, r.At, r.Checks, r.Acknowledged)
            : null;
        var agentProposals = state.AgentProposals(id);
        var queue = state.ProposalsFor(payment);
        var (invoices, comparisons) = Invoices(
            state,
            payment,
            new[] { record?.Decision.InvoiceNumbers, review?.InvoiceNumbers }
                .Concat(queue.Select(q => q.InvoiceNumbers))
                .Concat(agentProposals.Select(p => p.InvoiceNumbers)));
        return new PaymentDetailView(
            Item(state, payment),
            decision,
            agentProposals,
            review,
            state.IsActionable(id),
            queue,
            Timeline(state, payment, review),
            invoices,
            comparisons)
        {
            CanUndoAi = state.CanUndoAi(id),
        };
    }

    /// <summary>
    /// The invoices the decision, the review and the proposals name – only those of this sandbox, through its
    /// filtered data – and each named set against the payment amount.
    /// </summary>
    private static (IReadOnlyList<InvoiceFacts>, IReadOnlyList<InvoiceComparison>) Invoices(
        SandboxState state, BankPayment payment, IEnumerable<IReadOnlyList<string>?> namedSets)
    {
        var sets = namedSets
            .Where(s => s is { Count: > 0 })
            .Select(s => s!.Where(state.Data.InvoiceByNumber.ContainsKey).ToList())
            .Where(s => s.Count > 0)
            .DistinctBy(s => string.Join('\n', s))
            .ToList();
        var invoices = sets.SelectMany(s => s).Distinct(StringComparer.Ordinal)
            .Select(n => state.Data.InvoiceByNumber[n])
            .Select(i => new InvoiceFacts(
                i.Number,
                i.Kind,
                i.VariableSymbol,
                i.CustomerId,
                state.Data.CustomerById.TryGetValue(i.CustomerId, out var c) ? c.Name : "",
                i.DueDate,
                i.Amount,
                i.Currency,
                state.MatchedInvoices.TryGetValue(i.Number, out var paymentId) ? paymentId : null))
            .ToList();
        var comparisons = sets.Select(s =>
        {
            var named = s.Select(n => state.Data.InvoiceByNumber[n]).ToList();
            var currency = named[0].Currency;
            var sameCurrency = named.All(i => i.Currency == currency) && currency == payment.Currency;
            var total = named.Sum(i => i.Amount);
            return new InvoiceComparison(s, total, currency, sameCurrency ? payment.Amount - total : null);
        }).ToList();
        return (invoices, comparisons);
    }

    /// <summary>Payments a person should look at, oldest first.</summary>
    public static IReadOnlyList<PaymentListItem> Queue(SandboxState state) =>
        Items(state).Where(p => p.State == PaymentState.NeedsReview).ToList();

    public static IReadOnlyList<InvoiceOption> OpenInvoices(SandboxState state, string? freeing = null) => state.OpenInvoices(freeing)
        .Select(i => new InvoiceOption(i.Number, i.VariableSymbol, i.CustomerId, state.Data.CustomerById.TryGetValue(i.CustomerId, out var c) ? c.Name : "", i.DueDate, i.Amount, i.Currency))
        .ToList();

    public static DashboardView Dashboard(SandboxState state)
    {
        var states = state.Data.Payments.Select(p => state.StateOf(p.Id)).ToList();
        int Count(string s) => states.Count(x => x == s);
        var decisions = state.Decisions.ToList();
        var calls = decisions.SelectMany(d => d.Calls.Where(c => !c.Cached).Select(c => (Day: DateOnly.FromDateTime(d.At.UtcDateTime), c.CostUsd))).ToList();
        var costByDay = calls.GroupBy(c => c.Day).OrderBy(g => g.Key).Select(g => new DailyCost(g.Key, g.Count(), g.Sum(c => c.CostUsd))).ToList();
        return new DashboardView(
            state.Data.Payments.Count,
            decisions.Count,
            decisions.Count(d => d.Decision.Outcome == Outcome.AutoMatched),
            Count(PaymentState.NeedsReview),
            Count(PaymentState.NotMatched),
            Count(PaymentState.Rejected),
            Count(PaymentState.WaitingForAi),
            state.Reviews.Count(),
            decisions.Count(d => d.Decision.Outcome != Outcome.AutoMatched && d.Decision.Flags.Contains(Flags.PromptInjection)),
            calls.Count,
            calls.Sum(c => c.CostUsd),
            costByDay,
            LivePrecisionOf(state.Reviews))
        {
            CachedAnswers = decisions.Sum(d => d.Calls.Count(c => c.Cached)),
            AiBudgetUsedUp = decisions.Count(d => d.Decision.Flags.Contains(Flags.AiBudgetUsedUp) && state.StateOf(d.PaymentId) == PaymentState.NeedsReview),
            AiDecided = decisions.Count(d => d.Layer == AuditRecord.AiLayer),
            AiAutoMatched = decisions.Count(d => d.Layer == AuditRecord.AiLayer && d.Decision.Outcome == Outcome.AutoMatched),
            AiProposed = decisions.Count(d => d.Layer == AuditRecord.AiLayer && d.Decision.Outcome == Outcome.HumanReview && d.Decision.InvoiceNumbers.Count > 0),
            AiUndone = state.Reviews.Count(r => r.Action == ReviewAction.UndoneAi),
        };
    }

    /// <summary>
    /// For every proposal a person saw when deciding: accepted when the person matched exactly its
    /// invoices (confirmed it, or picked the same ones manually); otherwise not. Per proposer kind.
    /// </summary>
    public static IReadOnlyList<LivePrecision> LivePrecisionOf(IEnumerable<HumanDecision> reviews)
    {
        var votes = reviews.SelectMany(r => r.Proposals.SelectMany(p => p.ProposedBy.Distinct().Select(by => (
            Source: SourceOf(by),
            Accepted: r.Action != ReviewAction.Rejected && r.InvoiceNumbers.Count == p.InvoiceNumbers.Count && r.InvoiceNumbers.All(p.InvoiceNumbers.Contains)))));
        return votes.GroupBy(v => v.Source)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new LivePrecision(g.Key, g.Count(), g.Count(v => v.Accepted)))
            .ToList();
    }

    /// <summary>"rule:R3" → "rule", "ai:claude-…" → "ai", "agent:mcp" stays (each agent counts on its own).</summary>
    public static string SourceOf(string proposedBy) =>
        proposedBy.StartsWith("agent:", StringComparison.Ordinal) ? proposedBy : proposedBy.Split(':')[0];

    private static IReadOnlyList<TimelineEvent> Timeline(SandboxState state, BankPayment payment, ReviewView? review)
    {
        var events = new List<TimelineEvent>
        {
            new(new DateTimeOffset(payment.BookingDate, TimeOnly.MinValue, TimeSpan.Zero), TimelineKind.Received, "Demo Bank", T("Payment booked on the account."), [], []),
        };

        if (state.Decision(payment.Id) is { } record)
        {
            foreach (var call in record.Calls)
            {
                if (call.Cached)
                {
                    events.Add(new TimelineEvent(record.At, TimelineKind.ModelCall, "ai:" + call.Model, T("Answer reused from the response cache ({0}): {1}. The same request was answered before; no model was called and it cost nothing.", call.Tier, call.Outcome), [], [], "cache"));
                    continue;
                }

                events.Add(new TimelineEvent(
                    record.At,
                    TimelineKind.ModelCall,
                    "ai:" + call.Model,
                    T("Model call ({0}): {1}.", call.Tier, call.Outcome),
                    [],
                    [
                        T("Tokens in/out: {0} / {1}", call.Usage.InputTokens.ToString(CultureInfo.InvariantCulture), call.Usage.OutputTokens.ToString(CultureInfo.InvariantCulture)),
                        T("Cost: {0}", "$" + call.CostUsd.ToString("0.000000", CultureInfo.InvariantCulture)),
                        T("Latency: {0} s", call.Latency.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)),
                    ],
                    "ai"));
            }

            var details = new List<string> { T("Layer: {0}", record.Layer) };
            if (record.RuleCode is not null)
            {
                details.Add(T("Rule: {0} – {1}", record.RuleCode, Format.Rule(record.RuleCode)));
            }

            if (record.PromptVersion is not null)
            {
                details.Add(T("Prompt: {0}", record.PromptVersion));
            }

            if (record.Decision.Confidence is { } confidence)
            {
                details.Add(T("Confidence: {0}", confidence.ToString("0.00", CultureInfo.InvariantCulture)));
            }

            if (record.Candidates.Count > 0)
            {
                details.Add(T("Candidates: {0}", string.Join(", ", record.Candidates)));
            }

            if (record.ValidationFailures.Count > 0)
            {
                details.Add(T("Validation failed: {0}", string.Join(", ", record.ValidationFailures)));
            }

            if (record.Decision.Flags.Count > 0)
            {
                details.Add(T("Flags: {0}", string.Join(", ", record.Decision.Flags.Select(Format.Flag))));
            }

            details.Add(T("Reason: {0}", record.Decision.Reason));
            events.Add(new TimelineEvent(record.At, TimelineKind.Decided, record.Decision.DecidedBy, OutcomeText(record.Decision.Outcome), record.Decision.InvoiceNumbers, details));
        }

        foreach (var proposal in state.AgentProposals(payment.Id))
        {
            events.Add(new TimelineEvent(
                proposal.At,
                TimelineKind.Proposed,
                proposal.ProposedBy,
                T("Proposed a match for a person to review."),
                proposal.InvoiceNumbers,
                [
                    proposal.Checks.Count == 0 ? T("Checks at the time: all passed") : T("Checks at the time: {0}", string.Join(", ", proposal.Checks)),
                    T("Agent's reason: {0}", proposal.Reason),
                ]));
        }

        if (review is not null)
        {
            var details = new List<string>();
            if (review.Checks.Count > 0)
            {
                details.Add(T("Checks at the time: {0}", string.Join(", ", review.Checks)));
            }

            if (review.Acknowledged.Count > 0)
            {
                details.Add(T("Warnings accepted by the person: {0}", string.Join(", ", review.Acknowledged)));
            }

            events.Add(new TimelineEvent(review.At, TimelineKind.Reviewed, "person: " + review.DecidedBy, ActionText(review.Action), review.InvoiceNumbers, details));
        }

        return events.OrderBy(e => e.At).ToList();
    }

    public static string OutcomeText(Outcome outcome) => outcome switch
    {
        Outcome.AutoMatched => T("Matched automatically."),
        Outcome.HumanReview => T("Sent to a person."),
        _ => T("Not matched on purpose."),
    };

    public static string ActionText(string action) => action switch
    {
        ReviewAction.Confirmed => T("Confirmed the proposal."),
        ReviewAction.MatchedManually => T("Matched manually."),
        ReviewAction.UndoneAi => T("Undid the AI match."),
        _ => T("Rejected: not to be matched."),
    };

    private static PaymentListItem Item(SandboxState state, BankPayment p)
    {
        var record = state.Decision(p.Id);
        return new PaymentListItem(
            p.Id,
            p.BookingDate,
            p.Amount,
            p.Currency,
            p.CounterpartyName,
            p.VariableSymbol,
            p.Message,
            PaymentStatus.Of(record),
            state.Review(p.Id) is null ? record?.Decision.DecidedBy : "person",
            record?.Decision.Flags ?? [],
            state.StateOf(p.Id),
            state.InjectionSuspected(p));
    }
}
