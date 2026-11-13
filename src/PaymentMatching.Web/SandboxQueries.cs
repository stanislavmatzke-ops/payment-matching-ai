using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Matching;

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
public sealed record TimelineEvent(DateTimeOffset At, string Kind, string Actor, string Summary, IReadOnlyList<string> InvoiceNumbers, IReadOnlyList<string> Details);

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
    IReadOnlyList<TimelineEvent> Timeline);

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
        return new PaymentDetailView(
            Item(state, payment),
            decision,
            state.AgentProposals(id),
            review,
            state.IsActionable(id),
            state.ProposalsFor(payment),
            Timeline(state, payment, review));
    }

    /// <summary>Payments a person should look at, oldest first.</summary>
    public static IReadOnlyList<PaymentListItem> Queue(SandboxState state) =>
        Items(state).Where(p => p.State == PaymentState.NeedsReview).ToList();

    public static IReadOnlyList<InvoiceOption> OpenInvoices(SandboxState state) => state.OpenInvoices()
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
            new(new DateTimeOffset(payment.BookingDate, TimeOnly.MinValue, TimeSpan.Zero), TimelineKind.Received, "Demo Bank", "Payment booked on the account.", [], []),
        };

        if (state.Decision(payment.Id) is { } record)
        {
            foreach (var call in record.Calls)
            {
                if (call.Cached)
                {
                    events.Add(new TimelineEvent(record.At, TimelineKind.ModelCall, "ai:" + call.Model, $"Answer reused from the response cache ({call.Tier}): {call.Outcome}. The same request was answered before; no model was called and it cost nothing.", [], []));
                    continue;
                }

                events.Add(new TimelineEvent(
                    record.At,
                    TimelineKind.ModelCall,
                    "ai:" + call.Model,
                    $"Model call ({call.Tier}): {call.Outcome}.",
                    [],
                    [
                        $"Tokens in/out: {call.Usage.InputTokens.ToString(CultureInfo.InvariantCulture)} / {call.Usage.OutputTokens.ToString(CultureInfo.InvariantCulture)}",
                        $"Cost: ${call.CostUsd.ToString("0.000000", CultureInfo.InvariantCulture)}",
                        $"Latency: {call.Latency.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s",
                    ]));
            }

            var details = new List<string> { $"Layer: {record.Layer}" };
            if (record.RuleCode is not null)
            {
                details.Add($"Rule: {record.RuleCode}");
            }

            if (record.PromptVersion is not null)
            {
                details.Add($"Prompt: {record.PromptVersion}");
            }

            if (record.Decision.Confidence is { } confidence)
            {
                details.Add($"Confidence: {confidence.ToString("0.00", CultureInfo.InvariantCulture)}");
            }

            if (record.Candidates.Count > 0)
            {
                details.Add($"Candidates: {string.Join(", ", record.Candidates)}");
            }

            if (record.ValidationFailures.Count > 0)
            {
                details.Add($"Validation failed: {string.Join(", ", record.ValidationFailures)}");
            }

            if (record.Decision.Flags.Count > 0)
            {
                details.Add($"Flags: {string.Join(", ", record.Decision.Flags.Select(Format.Flag))}");
            }

            details.Add($"Reason: {record.Decision.Reason}");
            events.Add(new TimelineEvent(record.At, TimelineKind.Decided, record.Decision.DecidedBy, OutcomeText(record.Decision.Outcome), record.Decision.InvoiceNumbers, details));
        }

        foreach (var proposal in state.AgentProposals(payment.Id))
        {
            events.Add(new TimelineEvent(
                proposal.At,
                TimelineKind.Proposed,
                proposal.ProposedBy,
                "Proposed a match for a person to review.",
                proposal.InvoiceNumbers,
                [
                    proposal.Checks.Count == 0 ? "Checks at the time: all passed" : $"Checks at the time: {string.Join(", ", proposal.Checks)}",
                    $"Agent's reason: {proposal.Reason}",
                ]));
        }

        if (review is not null)
        {
            var details = new List<string>();
            if (review.Checks.Count > 0)
            {
                details.Add($"Checks at the time: {string.Join(", ", review.Checks)}");
            }

            if (review.Acknowledged.Count > 0)
            {
                details.Add($"Warnings accepted by the person: {string.Join(", ", review.Acknowledged)}");
            }

            events.Add(new TimelineEvent(review.At, TimelineKind.Reviewed, "person: " + review.DecidedBy, ActionText(review.Action), review.InvoiceNumbers, details));
        }

        return events.OrderBy(e => e.At).ToList();
    }

    public static string OutcomeText(Outcome outcome) => outcome switch
    {
        Outcome.AutoMatched => "Matched automatically.",
        Outcome.HumanReview => "Sent to a person.",
        _ => "Not matched on purpose.",
    };

    public static string ActionText(string action) => action switch
    {
        ReviewAction.Confirmed => "Confirmed the proposal.",
        ReviewAction.MatchedManually => "Matched manually.",
        _ => "Rejected: not to be matched.",
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
