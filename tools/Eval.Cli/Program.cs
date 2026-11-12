using System.Globalization;
using Eval.Cli;
using Microsoft.Extensions.Configuration;
using PaymentMatching.AiGateway;
using PaymentMatching.Evals;

// Runs the matcher in several configurations over the frozen gold set, scores it against the ground
// truth, writes a Markdown report and fails (exit 1) when the CI gate is not met (ADR 0008).
//
//   eval [--dataset evals/dataset/gold-v1] [--mode replay|record|live] [--configs rules,stub,...]
//        [--recordings evals/recordings] [--repeat <n>] [--min-precision 0.99] [--report <file>]
//
// Modes: replay – recorded model answers only (CI, no key); a missing recording is an error
//        record – recorded answers, missing ones fetched live and saved (needs a key, costs money)
//        live   – real model, nothing saved (needs a key, costs money); --repeat n runs the
//                 production config n times and reports the run-to-run variance
var options = EvalOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: eval [--dataset <dir>] [--mode replay|record|live] [--configs a,b] [--recordings <dir>] [--repeat <n>] [--min-precision 0.99] [--report <file>]");
    return 2;
}

if (options.Repeat > 1 && options.Mode != "live")
{
    Console.Error.WriteLine("--repeat measures run-to-run variance and needs --mode live (recorded answers never vary).");
    return 2;
}

var manifestProblems = GoldSet.VerifyManifest(options.Dataset);
if (manifestProblems.Count > 0)
{
    Console.Error.WriteLine($"Gold set {options.Dataset} does not match its {GoldSet.ManifestFile} (a frozen set must not change; create a new version instead):");
    manifestProblems.ToList().ForEach(p => Console.Error.WriteLine("  " + p));
    return 2;
}

var gold = GoldSet.Load(options.Dataset);
var configs = options.Configs.Select(EvalConfig.ByName).ToList();

ILlmProvider? model = null;
if (configs.Any(c => c.Layer2 == Layer2.Model))
{
    try
    {
        model = options.Mode switch
        {
            "replay" => new RecordReplayProvider(options.Recordings, RecordingMode.Replay),
            "record" => new RecordReplayProvider(options.Recordings, RecordingMode.Record, Live()),
            "live" => Live(),
            _ => throw new ArgumentException($"Unknown mode '{options.Mode}'."),
        };
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

Console.WriteLine($"EVAL ({options.Mode}) {gold.Name}: {gold.Data.Payments.Count} payments, {gold.Data.Invoices.Count} invoices");
var runs = new List<EvalRun>();
foreach (var config in configs)
{
    runs.Add(await RunAsync(config));
}

// Free threshold sweep: same requests, so recorded answers replay. Not in live mode (it would pay again).
var sweep = new List<EvalResult>();
if (options.Mode != "live" && configs.Any(c => c.Name == EvalConfig.Production))
{
    var production = EvalConfig.ByName(EvalConfig.Production);
    foreach (var threshold in new[] { 0.80, 0.85, 0.88, 0.90, 0.95 })
    {
        sweep.Add((await EvalRunner.RunAsync(gold, production.WithAutoMatchThreshold(threshold), model)).Result);
    }
}

VarianceScore? variance = null;
if (options.Repeat > 1)
{
    var main = runs.FirstOrDefault(r => r.Config.Name == EvalConfig.Production)
        ?? throw new InvalidOperationException($"--repeat needs the '{EvalConfig.Production}' config.");
    var repeated = new List<EvalRun> { main };
    for (var i = 1; i < options.Repeat; i++)
    {
        repeated.Add(await RunAsync(main.Config));
    }

    variance = VarianceScore.Of(repeated);
}

var violations = EvalGate.Check(runs.Select(r => r.Result), options.MinPrecision);
var date = DateOnly.FromDateTime(DateTime.Now);
var reportPath = options.Report ?? (options.Mode == "replay"
    ? Path.Combine("out", "eval", "report.md")
    : Path.Combine("evals", "reports", MarkdownReport.FileName(date)));
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
File.WriteAllText(reportPath, MarkdownReport.Write(new ReportInput(gold.Name, options.Mode, date, runs, sweep, variance, violations, options.MinPrecision)));

Console.WriteLine();
Console.WriteLine($"Report: {reportPath}");
if (violations.Count > 0)
{
    Console.WriteLine("GATE FAILED:");
    violations.ToList().ForEach(v => Console.WriteLine("  " + v));
    return 1;
}

Console.WriteLine("Gate passed.");
return 0;

async Task<EvalRun> RunAsync(EvalConfig config)
{
    var run = await EvalRunner.RunAsync(gold, config, model);
    var r = run.Result;
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"  {config.Name,-11} precision {r.Precision,7:P1}  automation {r.Automation,7:P1}  wrong {r.WrongInvoices,2}  injections defended {r.Injection.Defended}/{r.Injection.Total}  AI payments {r.Cost.AiPayments,3}  large calls {r.Cost.LargeCalls,3}  ${r.Cost.TotalUsd:0.0000}"));
    return run;
}

AnthropicProvider Live()
{
    var config = new ConfigurationBuilder()
        .AddUserSecrets("payment-matching-ai")
        .AddEnvironmentVariables()
        .Build();
    var key = config["Anthropic:ApiKey"] ?? config["ANTHROPIC_API_KEY"];
    if (string.IsNullOrWhiteSpace(key))
    {
        throw new InvalidOperationException(
            "No API key. Run: dotnet user-secrets set \"Anthropic:ApiKey\" \"<key>\" --id payment-matching-ai (or set ANTHROPIC_API_KEY).");
    }

    return AnthropicProvider.Create(key, PaymentMatching.Matching.ModelSettings.Default.CallTimeout);
}

namespace Eval.Cli
{
    internal sealed record EvalOptions(string Dataset, string Mode, IReadOnlyList<string> Configs, string Recordings, int Repeat, double MinPrecision, string? Report)
    {
        public static EvalOptions? Parse(string[] args)
        {
            if (args.Length % 2 != 0)
            {
                return null;
            }

            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    return null;
                }

                named[args[i]] = args[i + 1];
            }

            var mode = named.GetValueOrDefault("--mode", "replay");
            return mode is "replay" or "record" or "live"
                ? new EvalOptions(
                    named.GetValueOrDefault("--dataset", Path.Combine("evals", "dataset", "gold-v1")),
                    mode,
                    named.GetValueOrDefault("--configs", string.Join(',', EvalConfig.All.Select(c => c.Name))).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    named.GetValueOrDefault("--recordings", Path.Combine("evals", "recordings")),
                    int.Parse(named.GetValueOrDefault("--repeat", "1"), CultureInfo.InvariantCulture),
                    double.Parse(named.GetValueOrDefault("--min-precision", "0.99"), CultureInfo.InvariantCulture),
                    named.GetValueOrDefault("--report"))
                : null;
        }
    }
}
