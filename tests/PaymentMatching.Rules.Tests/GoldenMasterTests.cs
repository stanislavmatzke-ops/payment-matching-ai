namespace PaymentMatching.Rules.Tests;

/// <summary>
/// Characterization tests against the legacy golden master (legacy/golden, produced by the
/// Free Pascal build). The port was byte-identical with legacy (git tag phase3-faithful-port).
/// Since then every line that differs must be explained by a documented change (F1, F2).
/// </summary>
public class GoldenMasterTests
{
    public static TheoryData<string, string> Datasets => new()
    {
        { "data/sample", "legacy/golden/seed-42" },
        { "legacy/golden/seed-2005/input", "legacy/golden/seed-2005" },
    };

    [Theory]
    [MemberData(nameof(Datasets))]
    public void Every_difference_from_legacy_is_explained_by_a_documented_change(string inputDir, string goldenDir)
    {
        var legacy = Rows(LegacyCsv.ReadText(RepoPaths.Combine(goldenDir, "legacy_matches.csv")));
        var port = Rows(GoldenRun.Port(inputDir));
        var targetInvoice = GoldenRun.InvoiceByPaymentVs(inputDir);
        Assert.Equal(legacy.Keys, port.Keys);

        // Invoices that legacy gave away through the removed substring lookup.
        var fuzzyInvoices = legacy.Values.Where(r => r.Rule.StartsWith("FUZZY/", StringComparison.Ordinal) && r.Invoice.Length > 0)
            .Select(r => r.Invoice)
            .ToHashSet();

        var unexplained = new List<string>();
        var explained = 0;
        foreach (var (id, before) in legacy)
        {
            var after = port[id];
            if (before == after)
            {
                continue;
            }

            if (IsF1LookupRemoved(before, after)
                || IsF1InvoiceNoLongerTaken(before, after, targetInvoice.GetValueOrDefault(id), fuzzyInvoices))
            {
                explained++;
            }
            else
            {
                unexplained.Add($"  legacy: {before}\n  port:   {after}");
            }
        }

        Assert.True(unexplained.Count == 0, $"{unexplained.Count} unexplained differences:\n{string.Join('\n', unexplained.Take(20))}");
        Assert.True(explained > 0, "Expected the F1 change to show up in the golden data.");
    }

    [Theory]
    [MemberData(nameof(Datasets))]
    public void Committed_port_output_is_up_to_date(string inputDir, string goldenDir)
    {
        var committed = LegacyCsv.ReadText(RepoPaths.Combine(goldenDir, "port_matches.csv"));

        Assert.True(committed == GoldenRun.Port(inputDir),
            $"Regenerate with: dotnet run --project tools/Rules.Cli -- {inputDir} {goldenDir}/port_matches.csv");
    }

    /// <summary>F1: a payment legacy matched through the substring lookup is now not found.</summary>
    private static bool IsF1LookupRemoved(Row before, Row after) =>
        before.Rule.StartsWith("FUZZY/", StringComparison.Ordinal)
        && after is { Status: "N", Rule: "FIELD/NOT_FOUND" };

    /// <summary>
    /// F1 knock-on: legacy rejected this payment as a duplicate only because a fuzzy match had
    /// taken its invoice. The port now evaluates it normally (it may match, or be e.g. overpaid).
    /// </summary>
    private static bool IsF1InvoiceNoLongerTaken(Row before, Row after, string? invoice, HashSet<string> fuzzyInvoices) =>
        before.Rule.EndsWith("/DUPLICATE", StringComparison.Ordinal)
        && !after.Rule.EndsWith("/DUPLICATE", StringComparison.Ordinal)
        && invoice is not null
        && fuzzyInvoices.Contains(invoice);

    private static Dictionary<string, Row> Rows(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => line.Split(';'))
            .ToDictionary(f => f[0], f => new Row(f[1], f[2], f[3], f[4]));

    private sealed record Row(string Status, string Invoice, string Rule, string Diff);
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

    /// <summary>Payment ID → invoice whose VS exactly equals the payment's VS (field, or message when the field is empty).</summary>
    public static Dictionary<string, string> InvoiceByPaymentVs(string inputDir)
    {
        var dir = RepoPaths.Combine(inputDir);
        var invoiceByVs = LegacyCsv.ReadInvoices(Path.Combine(dir, LegacyCsv.InvoicesFile))
            .GroupBy(i => i.VariableSymbol)
            .ToDictionary(g => g.Key, g => g.First().Number);

        return LegacyCsv.ReadPayments(Path.Combine(dir, LegacyCsv.StatementFile))
            .Select(p => (p.Id, Vs: VariableSymbol.Normalize(p.VariableSymbol) is { Length: > 0 } vs ? vs : MessageVsParser.Extract(p.Message)))
            .Where(x => invoiceByVs.ContainsKey(x.Vs))
            .ToDictionary(x => x.Id, x => invoiceByVs[x.Vs]);
    }
}
