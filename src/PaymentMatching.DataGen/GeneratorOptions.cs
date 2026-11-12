using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen;

public sealed record GeneratorOptions
{
    /// <summary>Default share of each case type. Exact matches dominate, as in real statements.</summary>
    public static readonly IReadOnlyDictionary<CaseType, double> DefaultCaseMix = new Dictionary<CaseType, double>
    {
        [CaseType.ExactMatch] = 0.45,
        [CaseType.VsInMessage] = 0.10,
        [CaseType.VsTypo] = 0.08,
        [CaseType.MultiInvoice] = 0.08,
        [CaseType.PartialOrOverpayment] = 0.08,
        [CaseType.ThirdPartyPayer] = 0.06,
        [CaseType.ForeignCurrency] = 0.05,
        [CaseType.Unrelated] = 0.05,
        [CaseType.PromptInjection] = 0.05,
    };

    public int Seed { get; init; } = 42;

    public int PaymentCount { get; init; } = 300;

    /// <summary>Statement end date. Nothing is generated after it; the generator never reads the clock.</summary>
    public DateOnly ReferenceDate { get; init; } = new(2026, 3, 31);

    /// <summary>Extra unpaid invoices per payment. They act as distractors for any matcher.</summary>
    public double OpenInvoiceRatio { get; init; } = 0.25;

    public IReadOnlyDictionary<CaseType, double> CaseMix { get; init; } = DefaultCaseMix;

    public void Validate()
    {
        if (PaymentCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PaymentCount), "At least one payment is required.");
        }

        if (OpenInvoiceRatio < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(OpenInvoiceRatio), "Must not be negative.");
        }

        if (CaseMix.Values.Any(w => w < 0) || Math.Abs(CaseMix.Values.Sum() - 1.0) > 1e-9)
        {
            throw new ArgumentException("Case mix weights must be non-negative and sum to 1.", nameof(CaseMix));
        }
    }
}
