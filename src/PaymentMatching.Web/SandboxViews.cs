using PaymentMatching.Data;
using PaymentMatching.Matching;

namespace PaymentMatching.Web;

// The read-only views of a sandbox that the pages and /api render (built by SandboxQueries).

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
