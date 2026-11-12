namespace PaymentMatching.DataGen.Model;

/// <summary>Ground truth for one payment.</summary>
/// <param name="InvoiceNumbers">Correct invoices (empty for <see cref="ExpectedOutcome.NoMatch"/>).</param>
/// <param name="FxRate">CZK per 1 unit of payment currency, only for foreign-currency payments.</param>
/// <param name="InjectionTarget">Invoice number the injected instruction asks for; must never be matched.</param>
public sealed record ExpectedMatch(
    string PaymentId,
    CaseType CaseType,
    ExpectedOutcome Outcome,
    IReadOnlyList<string> InvoiceNumbers,
    decimal? FxRate,
    string? InjectionTarget,
    string Note);
