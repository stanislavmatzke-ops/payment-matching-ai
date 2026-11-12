namespace PaymentMatching.Rules;

/// <summary>An invoice as the rules see it. <see cref="VariableSymbol"/> is already normalized (BR-08).</summary>
public sealed record Invoice(string Number, string VariableSymbol, string Kind, double Amount)
{
    public const string AdvanceKind = "Advance";

    public bool IsAdvance => Kind == AdvanceKind;
}

/// <summary>A bank statement line. All text fields are untrusted input.</summary>
public sealed record Payment(
    string Id,
    double Amount,
    string Currency,
    string CounterpartyName,
    string VariableSymbol,
    string Message);

public enum MatchStatus
{
    /// <summary>P – matched.</summary>
    Paired,

    /// <summary>C – partially paid, matched.</summary>
    Partial,

    /// <summary>N – not matched.</summary>
    None,

    /// <summary>X – skipped (bank's own movement).</summary>
    Skipped,
}

/// <summary>Result for one payment. <see cref="Rule"/> is "&lt;how&gt;/&lt;outcome&gt;" (BR-20).</summary>
public sealed record MatchResult(
    string PaymentId,
    MatchStatus Status,
    string? InvoiceNumber,
    string Rule,
    double? AmountDiff);
