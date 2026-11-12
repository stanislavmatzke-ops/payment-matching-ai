using System.Globalization;
using PaymentMatching.Matching;

namespace Match.Cli;

/// <summary>
/// Quick check against the generator's expected.csv when it is next to the input. A preview only:
/// the proper eval runner with per-case metrics and reports comes in phase 5.
/// </summary>
internal static class GroundTruth
{
    public static void PrintIfPresent(TextWriter o, string inputDir, IReadOnlyList<AuditRecord> records)
    {
        var path = Path.Combine(inputDir, "expected.csv");
        if (!File.Exists(path))
        {
            return;
        }

        var expected = File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0)
            .Select(MatchingDataset.SplitCsv)
            .ToDictionary(f => f[0], f => (Case: f[1], Outcome: f[2], Invoices: f[3].Split('|', StringSplitOptions.RemoveEmptyEntries).Order().ToArray()));

        var auto = records.Where(r => r.Decision.Outcome == Outcome.AutoMatched).ToList();
        var correct = auto.Count(r => expected[r.PaymentId].Invoices.SequenceEqual(r.Decision.InvoiceNumbers.Order()));
        var wrongInvoice = auto.Count - correct;
        var shouldBeHuman = auto.Count(r => expected[r.PaymentId].Outcome != "AutoMatch" && expected[r.PaymentId].Invoices.SequenceEqual(r.Decision.InvoiceNumbers.Order()));
        var safe = correct - shouldBeHuman;

        o.WriteLine();
        o.WriteLine("Against ground truth (expected.csv, preview of the phase 5 eval):");
        o.WriteLine($"  auto-matched {auto.Count}: correct and safe {safe}, correct invoice but should be a person {shouldBeHuman}, WRONG invoice {wrongInvoice}");
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  precision of automatic matches {100.0 * safe / Math.Max(1, auto.Count):0.0} %"));
        o.WriteLine("  by case:            auto  person  none");
        foreach (var group in records.GroupBy(r => expected[r.PaymentId].Case).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            o.WriteLine($"    {group.Key,-20} {group.Count(r => r.Decision.Outcome == Outcome.AutoMatched),4} {group.Count(r => r.Decision.Outcome == Outcome.HumanReview),7} {group.Count(r => r.Decision.Outcome == Outcome.NoMatch),5}");
        }
    }
}
