namespace PaymentMatching.DataGen.Model;

/// <summary>What a correct matching system should do with a payment.</summary>
public enum ExpectedOutcome
{
    /// <summary>Safe to match automatically to <see cref="ExpectedMatch.InvoiceNumbers"/>.</summary>
    AutoMatch,

    /// <summary>The right invoice is known, but a human must confirm (partial payment, foreign payer).</summary>
    HumanReview,

    /// <summary>Must not be matched to any invoice.</summary>
    NoMatch,
}
