namespace PaymentMatching.Matching;

public sealed record Customer(string Id, string Name, string Iban);

public sealed record InvoiceRecord(
    string Number,
    string VariableSymbol,
    string CustomerId,
    string Kind,
    DateOnly DueDate,
    decimal Amount,
    string Currency);

/// <summary>
/// One bank statement line. Every text field is untrusted input. <see cref="RawLine"/> is the
/// line as received; it is the basis of the idempotency key.
/// </summary>
public sealed record BankPayment(
    string Id,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    string CounterpartyIban,
    string CounterpartyName,
    string VariableSymbol,
    string Message,
    string RawLine);

public enum Outcome
{
    /// <summary>Matched without a human (by a rule or by AI that passed validation).</summary>
    AutoMatched,

    /// <summary>Waits for a person; may carry a proposal and the reason.</summary>
    HumanReview,

    /// <summary>Not matched on purpose (bank's own movement, unrelated payment, captured prompt injection).</summary>
    NoMatch,
}

/// <summary>
/// Who decided or proposed: a rule (<c>rule:CODE</c>), a model (<c>ai:MODEL</c>), an agent (<c>agent:NAME</c>),
/// the policy layer, or a person (<c>person</c>, in the app optionally <c>person: NAME</c>). The values are
/// stored in audit records, so they never change; <see cref="KindOf"/> is the one place that reads them.
/// </summary>
public static class DecidedBy
{
    public const string RuleKind = "rule";
    public const string AiKind = "ai";
    public const string AgentKind = "agent";
    public const string PolicyKind = "policy";
    public const string PersonKind = "person";

    public const string Policy = PolicyKind;
    public const string Person = PersonKind;

    private const string RulePrefix = RuleKind + ":";
    private const string AiPrefix = AiKind + ":";
    private const string AgentPrefix = AgentKind + ":";

    public static string Rule(string ruleCode) => RulePrefix + ruleCode;

    public static string Ai(string model) => AiPrefix + model;

    public static string Agent(string name) => AgentPrefix + name;

    public static bool IsAi(string decidedBy) => decidedBy.StartsWith(AiPrefix, StringComparison.Ordinal);

    /// <summary>One of the <c>*Kind</c> constants; anything unknown counts as the policy.</summary>
    public static string KindOf(string decidedBy) => decidedBy switch
    {
        var v when v.StartsWith(RulePrefix, StringComparison.Ordinal) => RuleKind,
        var v when v.StartsWith(AiPrefix, StringComparison.Ordinal) => AiKind,
        var v when v.StartsWith(AgentPrefix, StringComparison.Ordinal) => AgentKind,
        var v when v.StartsWith(PersonKind, StringComparison.Ordinal) => PersonKind,
        _ => PolicyKind,
    };

    /// <summary>What follows the kind: the rule code, the model, the agent's or the person's name ("" if none).</summary>
    public static string Detail(string decidedBy) => KindOf(decidedBy) switch
    {
        RuleKind => decidedBy[RulePrefix.Length..],
        AiKind => decidedBy[AiPrefix.Length..],
        AgentKind => decidedBy[AgentPrefix.Length..],
        PersonKind => decidedBy[PersonKind.Length..].TrimStart(':').Trim(),
        _ => "",
    };
}

/// <summary>Risk signals shown to the reviewer and counted on the dashboard.</summary>
public static class Flags
{
    public const string PromptInjection = "prompt_injection";
    public const string ThirdPartyPayer = "third_party_payer";
    public const string PartialPayment = "partial_payment";
    public const string Overpayment = "overpayment";
    public const string CurrencyConversion = "currency_conversion";
    public const string VsTypo = "vs_typo";
    public const string MultiInvoice = "multi_invoice";

    /// <summary>
    /// Set by code, never by a model (not in <see cref="All"/>, so not in the answer schema): the app-wide AI
    /// budget was used up, so the payment went to a person without asking a model (ADR 0013).
    /// </summary>
    public const string AiBudgetUsedUp = "ai_budget_used_up";

    public static IReadOnlyList<string> All { get; } =
        [PromptInjection, ThirdPartyPayer, PartialPayment, Overpayment, CurrencyConversion, VsTypo, MultiInvoice];

    /// <summary>Flags that always need a person, whatever the confidence.</summary>
    public static IReadOnlySet<string> NeedHuman { get; } =
        new HashSet<string> { PromptInjection, ThirdPartyPayer, PartialPayment, Overpayment };
}

/// <summary>Final decision for one payment. <see cref="InvoiceNumbers"/> is the match or, for review, the proposal.</summary>
public sealed record Decision(
    string PaymentId,
    Outcome Outcome,
    IReadOnlyList<string> InvoiceNumbers,
    string DecidedBy,
    string Reason,
    IReadOnlyList<string> Flags,
    double? Confidence = null);

/// <summary>Where a payment stands, as recorded by the matching pipeline (or not processed yet).</summary>
public static class PaymentStatus
{
    public const string AutoMatched = "auto_matched";
    public const string HumanReview = "human_review";
    public const string NoMatch = "no_match";
    public const string NotProcessed = "not_processed";

    public static string Of(AuditRecord? record) => record?.Decision.Outcome switch
    {
        Outcome.AutoMatched => AutoMatched,
        Outcome.HumanReview => HumanReview,
        Outcome.NoMatch => NoMatch,
        _ => NotProcessed,
    };
}
