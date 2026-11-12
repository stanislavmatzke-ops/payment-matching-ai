using System.Globalization;
using PaymentMatching.AiGateway;
using PaymentMatching.Matching;

namespace Match.Cli;

/// <summary>Console summary: decisions by layer, AI calls, escalations, tokens, cost, latency.</summary>
internal static class Summary
{
    public static void Print(TextWriter o, CliOptions options, MatchingDataset data, RunReport report)
    {
        var inv = CultureInfo.InvariantCulture;
        var records = report.Records;
        o.WriteLine($"MATCH ({options.Mode}): {data.Invoices.Count} invoices, {data.Payments.Count} payments, {report.Reused} already decided earlier");

        o.WriteLine();
        o.WriteLine("Decisions by layer:");
        foreach (var group in records.GroupBy(r => (r.Layer, r.Decision.Outcome)).OrderBy(g => g.Key.Layer, StringComparer.Ordinal).ThenBy(g => g.Key.Outcome))
        {
            o.WriteLine($"  {group.Key.Layer,-7} {group.Key.Outcome,-12} {group.Count(),5}");
        }

        var auto = records.Count(r => r.Decision.Outcome == Outcome.AutoMatched);
        o.WriteLine(string.Create(inv, $"  automation: {auto} of {records.Count} ({100.0 * auto / Math.Max(1, records.Count):0.0} %) matched without a person"));
        o.WriteLine($"  prompt injection flagged: {records.Count(r => r.Decision.Flags.Contains(Flags.PromptInjection))}");

        // Only calls made in this run (reused records carry their original calls).
        var calls = records.Skip(0).Where(r => r.Calls.Count > 0).ToList();
        var allCalls = calls.SelectMany(r => r.Calls).ToList();
        o.WriteLine();
        o.WriteLine("AI:");
        o.WriteLine($"  payments sent to AI: {calls.Count}, calls: {allCalls.Count}, escalated to the large model: {calls.Count(r => r.Calls.Any(c => c.Tier == "large"))}");
        foreach (var model in allCalls.GroupBy(c => c.Model))
        {
            var tokensIn = model.Sum(c => c.Usage.InputTokens);
            var tokensOut = model.Sum(c => c.Usage.OutputTokens);
            o.WriteLine(string.Create(inv, $"  {model.Key,-18} calls {model.Count(),4}  tokens in {tokensIn,8} out {tokensOut,7}  cost ${model.Sum(c => c.CostUsd):0.0000}"));
        }

        foreach (var outcome in allCalls.GroupBy(c => c.Outcome).OrderBy(g => g.Key))
        {
            o.WriteLine($"  call outcome {outcome.Key,-14} {outcome.Count(),4}");
        }

        var cost = allCalls.Sum(c => c.CostUsd);
        o.WriteLine(string.Create(inv, $"  total cost ${cost:0.0000}, per 1 000 payments ${cost * 1000 / Math.Max(1, records.Count):0.00}"));
        var latencies = calls.Select(r => r.Calls.Sum(c => c.Latency.TotalMilliseconds)).Order().ToList();
        if (latencies.Count > 0 && latencies[^1] > 0)
        {
            o.WriteLine(string.Create(inv, $"  latency per AI payment p50 {Percentile(latencies, 0.50):0} ms, p95 {Percentile(latencies, 0.95):0} ms"));
        }

        GroundTruth.PrintIfPresent(o, options.InputDir, records);
        o.WriteLine();
        o.WriteLine($"Written: {Path.Combine(options.OutDir, "matches.csv")}, {Path.Combine(options.OutDir, "audit.jsonl")}");
    }

    private static double Percentile(List<double> sorted, double p) => sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
}
