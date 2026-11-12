namespace PaymentMatching.Matching;

/// <summary>Business settings of layer 2 (ADR 0006). Defaults follow the decisions on Q-02 and Q-07.</summary>
public sealed record MatchingOptions
{
    /// <summary>Q-02: partial payments and overpaid advances go to a person instead of being matched automatically.</summary>
    public bool PartialPaymentsNeedHuman { get; init; } = true;

    /// <summary>Q-07: a match without a VS in the VS field needs a payer that is the invoice customer.</summary>
    public bool RequireCustomerPayer { get; init; } = true;

    /// <summary>Minimum model confidence for an automatic match (after validation passed).</summary>
    public double AutoMatchConfidence { get; init; } = 0.90;

    /// <summary>Below this confidence the small model's answer is escalated to the large model.</summary>
    public double EscalationConfidence { get; init; } = 0.90;

    /// <summary>Amount tolerance in CZK (BR-15, strictly less than).</summary>
    public decimal Tolerance { get; init; } = 1.00m;

    /// <summary>Plausible CZK per EUR. A real deployment would use the daily CNB rate plus a margin.</summary>
    public decimal MinEurRate { get; init; } = 24.00m;

    public decimal MaxEurRate { get; init; } = 26.50m;

    /// <summary>Most invoices the model ever sees for one payment.</summary>
    public int MaxCandidates { get; init; } = 8;

    public static MatchingOptions Default { get; } = new();
}
