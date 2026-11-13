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

    /// <summary>
    /// Ask the large model only when some candidate set could still pass validation, i.e. when its answer
    /// can change the outcome. Otherwise (overpayment, third-party payer, no fitting amount) the payment
    /// goes to a person whatever the model says, and the small model's proposal is enough. On the gold set:
    /// same precision and automation with 4 instead of 39 large-model calls (ADR 0009).
    /// </summary>
    public bool EscalateOnlyWhenAutoMatchPossible { get; init; } = true;

    /// <summary>Amount tolerance in CZK (BR-15, strictly less than).</summary>
    public decimal Tolerance { get; init; } = 1.00m;

    /// <summary>Plausible CZK per EUR. A real deployment would use the daily CNB rate plus a margin.</summary>
    public decimal MinEurRate { get; init; } = 24.00m;

    public decimal MaxEurRate { get; init; } = 26.50m;

    /// <summary>
    /// Without a gateway, leave payments that need layer 2 undecided (not in the decision store) instead
    /// of sending them to a person. The hosted app runs the rules layer this way first, so a visitor never
    /// triggers a model call; the AI layer then answers from the response cache or waits for the job (ADR 0017).
    /// </summary>
    public bool DeferAiWithoutGateway { get; init; }

    /// <summary>
    /// When the gateway's call budget is used up before any model was asked, leave the payment undecided
    /// for a later run instead of sending it to a person. The daily job sets it when only the per-sandbox
    /// daily limit stops the call; when the app-wide budget is used up the payment goes to a person with
    /// <see cref="Flags.AiBudgetUsedUp"/> (ADR 0013).
    /// </summary>
    public bool DeferAiWhenBudgetExhausted { get; init; }

    /// <summary>Most invoices the model ever sees for one payment.</summary>
    public int MaxCandidates { get; init; } = 8;

    public static MatchingOptions Default { get; } = new();
}
