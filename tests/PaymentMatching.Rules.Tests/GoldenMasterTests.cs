namespace PaymentMatching.Rules.Tests;

/// <summary>
/// Characterization tests: the .NET port must produce the legacy output byte for byte
/// on the golden master datasets (legacy/golden, produced by the Free Pascal build).
/// </summary>
public class GoldenMasterTests
{
    public static TheoryData<string, string> Datasets => new()
    {
        { "data/sample", "legacy/golden/seed-42/legacy_matches.csv" },
        { "legacy/golden/seed-2005/input", "legacy/golden/seed-2005/legacy_matches.csv" },
    };

    [Theory]
    [MemberData(nameof(Datasets))]
    public void Port_reproduces_legacy_output_byte_for_byte(string inputDir, string legacyOutput)
    {
        var actual = GoldenRun.Port(inputDir);
        var expected = LegacyCsv.ReadText(RepoPaths.Combine(legacyOutput));

        var differences = GoldenRun.DiffLines(expected, actual);
        Assert.True(differences.Count == 0, $"{differences.Count} lines differ:\n{string.Join('\n', differences.Take(20))}");
        Assert.Equal(expected, actual);
    }
}

internal static class GoldenRun
{
    public static string Port(string inputDir)
    {
        var dir = RepoPaths.Combine(inputDir);
        var invoices = LegacyCsv.ReadInvoices(Path.Combine(dir, LegacyCsv.InvoicesFile));
        var payments = LegacyCsv.ReadPayments(Path.Combine(dir, LegacyCsv.StatementFile));
        return LegacyCsv.WriteResults(PaymentMatcher.Match(invoices, payments));
    }

    public static List<string> DiffLines(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var diffs = new List<string>();
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var left = i < e.Length ? e[i] : "<missing>";
            var right = i < a.Length ? a[i] : "<missing>";
            if (left != right)
            {
                diffs.Add($"  legacy: {left}\n  port:   {right}");
            }
        }

        return diffs;
    }
}
