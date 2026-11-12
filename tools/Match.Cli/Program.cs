using System.Globalization;
using Match.Cli;
using Microsoft.Extensions.Configuration;
using PaymentMatching.AiGateway;
using PaymentMatching.Matching;
using PaymentMatching.Observability;

// Runs the whole pipeline (rules → candidates → AI → validation) over a directory with
// customers.csv, invoices.csv and bank_statement.csv.
//
//   match <input-dir> [--out <dir>] [--mode stub|replay|record|live|rules] [--recordings <dir>] [--max-calls <n>]
//
// Modes: stub   – no model, deterministic stand-in (NaiveModelStub), no key needed
//        replay – recorded answers only (CI), no key needed
//        record – recorded answers, missing ones fetched live and saved (needs a key)
//        live   – real model, nothing saved (needs a key)
//        rules  – layer 1 only, AI switched off
// The audit log (<out>/audit.jsonl) is kept between runs: a payment that is already decided is
// not processed again (idempotency). Delete the file to start over.
//
//   match --tenant <id> [--mode stub]
//
// runs the pipeline with the stub model over one sandbox of the app database (see TenantRun).
if (args.Length > 0 && args[0] == "--tenant")
{
    return await TenantRun.RunAsync(args);
}

// Traces and metrics only when OTEL_EXPORTER_OTLP_ENDPOINT is set (e.g. the local Aspire dashboard).
using var telemetry = TelemetrySetup.StartConsole("payments-match", new ConfigurationBuilder().AddEnvironmentVariables().Build());

var options = CliOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: match <input-dir> [--out <dir>] [--mode stub|replay|record|live|rules] [--recordings <dir>] [--max-calls <n>]");
    return 2;
}

var data = MatchingDataset.Load(options.InputDir);
Directory.CreateDirectory(options.OutDir);

var gatewayOptions = ModelSettings.Default with { MaxCalls = options.MaxCalls };

ILlmProvider? provider;
try
{
    provider = options.Mode switch
    {
        "rules" => null,
        "stub" => new NaiveModelStub(),
        "replay" => new RecordReplayProvider(options.RecordingsDir, RecordingMode.Replay),
        "record" => new RecordReplayProvider(options.RecordingsDir, RecordingMode.Record, Live()),
        "live" => Live(),
        _ => throw new ArgumentException($"Unknown mode '{options.Mode}'."),
    };
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var gateway = provider is null ? null : new AiGateway(provider, gatewayOptions, PriceTable.Claude);
var log = new DecisionLog(Path.Combine(options.OutDir, "audit.jsonl"));
var report = await new MatchingPipeline(data, gateway, log, MatchingOptions.Default).RunAsync();

File.WriteAllText(Path.Combine(options.OutDir, "matches.csv"), ResultCsv.Write(report.Records));
Summary.Print(Console.Out, options, data, report);
return 0;

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

    return AnthropicProvider.Create(key, gatewayOptions.CallTimeout);
}

namespace Match.Cli
{
    internal sealed record CliOptions(string InputDir, string OutDir, string Mode, string RecordingsDir, int MaxCalls)
    {
        public static CliOptions? Parse(string[] args)
        {
            if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length - 1; i += 2)
            {
                named[args[i]] = args[i + 1];
            }

            return new CliOptions(
                args[0],
                named.GetValueOrDefault("--out", "out"),
                named.GetValueOrDefault("--mode", "stub"),
                named.GetValueOrDefault("--recordings", Path.Combine("evals", "recordings")),
                int.Parse(named.GetValueOrDefault("--max-calls", "500"), CultureInfo.InvariantCulture));
        }
    }
}
