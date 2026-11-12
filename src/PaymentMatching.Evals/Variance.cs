using PaymentMatching.Matching;

namespace PaymentMatching.Evals;

/// <summary>How much repeated live runs of one configuration differ (a model is not deterministic).</summary>
/// <param name="Differing">Payments whose outcome or invoices were not the same in every run.</param>
public sealed record VarianceScore(
    string Config,
    int Runs,
    int AutoMin,
    int AutoMax,
    double PrecisionMin,
    double PrecisionMax,
    decimal CostPer1000Min,
    decimal CostPer1000Max,
    IReadOnlyList<string> Differing)
{
    public static VarianceScore Of(IReadOnlyList<EvalRun> runs)
    {
        if (runs.Count == 0)
        {
            throw new ArgumentException("No runs.", nameof(runs));
        }

        var differing = runs
            .SelectMany(run => run.Records)
            .GroupBy(r => r.PaymentId)
            .Where(g => g.Select(Signature).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        var results = runs.Select(r => r.Result).ToList();
        return new VarianceScore(
            runs[0].Config.Name,
            runs.Count,
            results.Min(r => r.Auto),
            results.Max(r => r.Auto),
            results.Min(r => r.Precision),
            results.Max(r => r.Precision),
            results.Min(r => r.Cost.Per1000Usd),
            results.Max(r => r.Cost.Per1000Usd),
            differing);
    }

    private static string Signature(AuditRecord r) =>
        $"{r.Decision.Outcome}:{string.Join('|', r.Decision.InvoiceNumbers.Order(StringComparer.Ordinal))}";
}
