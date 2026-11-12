using System.Globalization;
using System.Text;
using PaymentMatching.Matching;

namespace PaymentMatching.Evals;

/// <summary>Everything one eval report shows.</summary>
public sealed record ReportInput(
    string Dataset,
    string Mode,
    DateOnly Date,
    IReadOnlyList<EvalRun> Runs,
    IReadOnlyList<EvalResult> ThresholdSweep,
    VarianceScore? Variance,
    IReadOnlyList<string> GateViolations,
    double MinPrecision);

/// <summary>Markdown report, readable in a pull request and pasted into the README.</summary>
public static class MarkdownReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>File name: <c>&lt;date&gt;-&lt;models&gt;-&lt;prompt version&gt;.md</c>.</summary>
    public static string FileName(DateOnly date) =>
        $"{date:yyyy-MM-dd}-{ModelSettings.Default.Small.Model}+{ModelSettings.Default.Large!.Model.Replace("claude-", "", StringComparison.Ordinal)}-{PromptBuilder.Version}.md";

    public static string Write(ReportInput input)
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');
        var results = input.Runs.Select(r => r.Result).ToList();
        var first = results[0];

        Line($"# Eval report – {input.Dataset}, prompt {PromptBuilder.Version}");
        Line();
        Line($"- Date: {input.Date:yyyy-MM-dd}; mode: `{input.Mode}`; models: `{ModelSettings.Default.Small.Model}` → `{ModelSettings.Default.Large!.Model}`");
        Line(F($"- Dataset: {first.Total} payments; {first.GoldAuto} should be matched automatically (ceiling {first.Ceiling:P1})"));
        Line(F($"- Gate (CI): precision ≥ {input.MinPrecision:P0} and no injection matched for every config; `{EvalConfig.Production}` also never proposes an injection target → ")
             + (input.GateViolations.Count == 0 ? "**passed**" : "**FAILED**"));
        foreach (var violation in input.GateViolations)
        {
            Line($"  - {violation}");
        }

        Line();
        Line("## Summary");
        Line();
        Line("| Config | Precision (auto) | Automation | Wrong invoice | Should be a person | Injections defended | Proposal accuracy | AI payments | Large-model calls | Cost / 1 000 payments | Latency p50 / p95 |");
        Line("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var shouldBePerson = r.Errors.Count(e => e.Kind == ErrorKind.ShouldBePerson);
            Line(F($"| `{r.Config}` | **{r.Precision:P1}** ({r.AutoCorrect}/{r.Auto}) | {r.Automation:P1} | {r.WrongInvoices} | {shouldBePerson} | {r.Injection.Defended}/{r.Injection.Total} | {r.ProposalAccuracy:P0} ({r.ProposalsCorrect}/{r.ProposalsScored}) | {r.Cost.AiPayments} | {r.Cost.LargeCalls} | ${r.Cost.Per1000Usd:0.00} | {Latency(r.Cost)} |"));
        }

        Line();
        foreach (var run in input.Runs)
        {
            Line($"- `{run.Config.Name}`: {run.Config.Description}.");
        }

        Line();
        Line("Precision = automatic matches with exactly the right invoices that are allowed to be automatic. " +
             "Proposal accuracy = payments sent to a person whose prefilled proposal is exactly right (ambiguous cases excluded). " +
             "An injection is defended when it is not matched and its target is not proposed. Latency is per payment that reached the model.");

        Line();
        Line("## False match rate by case type");
        Line();
        Line("Share of payments matched automatically although they should not have been (wrong invoice or a person should decide).");
        Line();
        Line("| Case type | Payments | " + string.Join(" | ", results.Select(r => $"`{r.Config}`")) + " |");
        Line("|---|---|" + string.Concat(results.Select(_ => "---|")));
        foreach (var caseType in first.ByCase.Select(c => c.CaseType))
        {
            var cells = results.Select(r => r.ByCase.First(c => c.CaseType == caseType)).Select(c => F($"{c.FalseMatchRate:P0}"));
            Line($"| {caseType} | {first.ByCase.First(c => c.CaseType == caseType).Total} | {string.Join(" | ", cells)} |");
        }

        Line();
        Line("## Outcomes by case type (auto / person / no match)");
        Line();
        Line("| Case type | " + string.Join(" | ", results.Select(r => $"`{r.Config}`")) + " |");
        Line("|---|" + string.Concat(results.Select(_ => "---|")));
        foreach (var caseType in first.ByCase.Select(c => c.CaseType))
        {
            var cells = results.Select(r => r.ByCase.First(c => c.CaseType == caseType)).Select(c => $"{c.Auto} / {c.Person} / {c.None}");
            Line($"| {caseType} | {string.Join(" | ", cells)} |");
        }

        Line();
        Line("## Prompt injection");
        Line();
        Line("| Config | Injections | Matched | Target proposed to a person | Flagged `prompt_injection` |");
        Line("|---|---|---|---|---|");
        foreach (var r in results)
        {
            Line($"| `{r.Config}` | {r.Injection.Total} | {r.Injection.Matched} | {r.Injection.TargetProposed} | {r.Injection.Flagged} |");
        }

        var production = results.FirstOrDefault(r => r.Config == EvalConfig.Production);
        if (production is not null)
        {
            Line();
            Line($"## Calibration (`{production.Config}`)");
            Line();
            Line("Model answers that passed validation, by the model's confidence: how many named exactly the right invoices of a payment that may be matched automatically.");
            Line();
            Line("| Confidence | Answers | Right | Share right |");
            Line("|---|---|---|---|");
            foreach (var row in production.Calibration)
            {
                Line(F($"| {row.Bucket} | {row.Answers} | {row.Correct} | {(row.Answers == 0 ? "–" : ((double)row.Correct / row.Answers).ToString("P0", Inv))} |"));
            }
        }

        if (input.ThresholdSweep.Count > 0)
        {
            Line();
            Line("### Automatic-match threshold sweep (same recorded answers, escalation unchanged)");
            Line();
            Line("| Threshold | Precision | Automation | Wrong invoice | Should be a person |");
            Line("|---|---|---|---|---|");
            foreach (var r in input.ThresholdSweep)
            {
                Line(F($"| `{r.Config}` | {r.Precision:P1} | {r.Automation:P1} | {r.WrongInvoices} | {r.Errors.Count(e => e.Kind == ErrorKind.ShouldBePerson)} |"));
            }
        }

        if (input.Variance is { } v)
        {
            Line();
            Line($"## Run-to-run variance (`{v.Config}`, {v.Runs} live runs)");
            Line();
            Line("| | Min | Max |");
            Line("|---|---|---|");
            Line($"| Automatic matches | {v.AutoMin} | {v.AutoMax} |");
            Line(F($"| Precision | {v.PrecisionMin:P1} | {v.PrecisionMax:P1} |"));
            Line(F($"| Cost / 1 000 payments | ${v.CostPer1000Min:0.00} | ${v.CostPer1000Max:0.00} |"));
            Line();
            Line($"Payments with a different outcome or proposal in at least one run: {v.Differing.Count}" + (v.Differing.Count > 0 ? $" ({string.Join(", ", v.Differing)})." : "."));
        }

        Line();
        Line("## Errors");
        foreach (var r in results)
        {
            Line();
            Line($"### `{r.Config}`" + (r.Errors.Count == 0 ? ": none" : $": {r.Errors.Count}"));
            if (r.Errors.Count > 0)
            {
                Line();
                foreach (var e in r.Errors)
                {
                    Line($"- {e.PaymentId} ({e.CaseType}) {e.Kind}: {e.Detail}");
                }
            }
        }

        return sb.ToString();
    }

    private static string Latency(CostScore c) => c.AiPayments == 0 || c.LatencyP95Ms == 0
        ? "–"
        : F($"{c.LatencyP50Ms / 1000:0.0} s / {c.LatencyP95Ms / 1000:0.0} s");

    private static string F(FormattableString text) => text.ToString(Inv);
}
