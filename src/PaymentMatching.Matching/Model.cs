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

/// <summary>Who decided: a rule (with its code), the policy layer, or a model.</summary>
public static class DecidedBy
{
    public static string Rule(string ruleCode) => "rule:" + ruleCode;

    public static string Ai(string model) => "ai:" + model;

    public const string Policy = "policy";
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
