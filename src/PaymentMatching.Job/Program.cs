using Microsoft.Extensions.Configuration;
using PaymentMatching.AiGateway;
using PaymentMatching.Data;
using PaymentMatching.Job;
using PaymentMatching.Matching;

// payments-job: the scheduled daily job of the hosted app (SPEC 9b, ADR 0013/0014). One run, then exit;
// in Azure a Container Apps Job starts it on a cron schedule (phase 11). Configuration only from the
// environment or user-secrets: ConnectionStrings:Payments, Anthropic:ApiKey (live), Demo:TenantId, Ai:*.
const string Usage = "Usage: payments-job [--mode stub|live] [--migrate]   (stub: no model, no cost; live: real model calls within the AI budgets)";

var mode = "stub";
var migrate = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--mode" when i + 1 < args.Length && args[i + 1] is "stub" or "live":
            mode = args[++i];
            break;
        case "--migrate":
            migrate = true;
            break;
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}

var config = new ConfigurationBuilder().AddUserSecrets("payment-matching-ai").AddEnvironmentVariables().Build();
var connectionString = config.GetConnectionString(PaymentsDb.ConnectionStringName);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set ConnectionStrings:Payments (user-secrets --id payment-matching-ai or environment ConnectionStrings__Payments).");
    return 2;
}

Guid? demo = null;
if (config["Demo:TenantId"] is { Length: > 0 } demoSetting)
{
    if (!Guid.TryParse(demoSetting, out var demoId) || demoId == Guid.Empty)
    {
        Console.Error.WriteLine("Demo:TenantId must be a GUID.");
        return 2;
    }

    demo = demoId;
}

ILlmProvider model;
if (mode == "live")
{
    var key = config["Anthropic:ApiKey"] ?? config["ANTHROPIC_API_KEY"];
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine("--mode live needs Anthropic:ApiKey (user-secrets or environment Anthropic__ApiKey).");
        return 2;
    }

    model = AnthropicProvider.Create(key, ModelSettings.Default.CallTimeout);
}
else
{
    model = new NaiveModelStub();
}

if (migrate)
{
    await OperationsDb.MigrateAllAsync(connectionString);
}

var budget = AiBudgetOptions.Read(key => config[key]);
var job = new DailyJob(
    PaymentsDb.Options(connectionString),
    OperationsDb.Options(connectionString),
    model,
    new JobSettings(mode, demo, budget),
    TimeProvider.System,
    Console.Out);
var report = await job.RunAsync();

Console.WriteLine(
    $"Daily job {report.Day:yyyy-MM-dd} ({mode}): {report.Sandboxes.Count} sandboxes, {report.Sandboxes.Count(s => s.Refilled)} refilled, " +
    $"{report.ModelCalls} model calls, {report.CacheHits} cached answers, ${report.CostUsd:0.0000}, {report.Deleted} inactive deleted, " +
    $"demo {(demo is null ? "off" : report.DemoCreated ? "created" : "kept")}, {report.Failed} failed.");
return report.Failed == 0 ? 0 : 1;
