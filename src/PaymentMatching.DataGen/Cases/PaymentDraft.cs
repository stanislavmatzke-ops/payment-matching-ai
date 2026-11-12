using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>A payment plus its ground truth, before payment IDs are assigned.</summary>
internal sealed record PaymentDraft(
    CaseType CaseType,
    ExpectedOutcome Outcome,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    string CounterpartyIban,
    string CounterpartyName,
    string VariableSymbol,
    string Message,
    IReadOnlyList<string> InvoiceNumbers,
    string Note)
{
    public string ConstantSymbol { get; init; } = "";

    public string SpecificSymbol { get; init; } = "";

    public decimal? FxRate { get; init; }

    public string? InjectionTarget { get; init; }
}
