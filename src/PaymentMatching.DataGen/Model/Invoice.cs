namespace PaymentMatching.DataGen.Model;

/// <summary>An issued invoice. <see cref="InvoiceNumber"/> (e.g. 2026-1234) maps to a 10-digit variable symbol (2026001234).</summary>
public sealed record Invoice(
    string InvoiceNumber,
    string VariableSymbol,
    string CustomerId,
    InvoiceKind Kind,
    DateOnly IssueDate,
    DateOnly DueDate,
    decimal Amount,
    string Currency);
