using System.Globalization;
using PaymentMatching.Evals;
using PaymentMatching.Matching;

namespace Match.Cli;

/// <summary>
/// Quick check against the generator's expected.csv when it is next to the input, with the same
/// scorer as the eval runner (tools/Eval.Cli), which compares configurations and writes reports.
/// </summary>
internal static class GroundTruth
{
    public static void PrintIfPresent(TextWriter o, string inputDir, IReadOnlyList<AuditRecord> records)
    {
        if (!File.Exists(Path.Combine(inputDir, "expected.csv")))
        {
            return;
        }

        var r = Scorer.Score("match", GoldSet.Load(inputDir), records);
        o.WriteLine();
        o.WriteLine("Against ground truth (expected.csv):");
        o.WriteLine($"  auto-matched {r.Auto}: correct and safe {r.AutoCorrect}, correct invoice but should be a person {r.Errors.Count(e => e.Kind == ErrorKind.ShouldBePerson)}, WRONG invoice {r.WrongInvoices}");
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  precision of automatic matches {r.Precision:P1}, automation {r.Automation:P1} (ceiling {r.Ceiling:P1})"));
        o.WriteLine($"  injections defended {r.Injection.Defended}/{r.Injection.Total}, proposals for a person right {r.ProposalsCorrect}/{r.ProposalsScored}");
        o.WriteLine("  by case:              auto  person  none");
        foreach (var row in r.ByCase)
        {
            o.WriteLine($"    {row.CaseType,-20} {row.Auto,5} {row.Person,7} {row.None,5}");
        }
    }
}
