using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PaymentMatching.Data;
using PaymentMatching.Matching;

namespace Match.Cli;

/// <summary>
/// <c>match --tenant &lt;id&gt; [--mode stub]</c>: runs the pipeline over one sandbox of the app database,
/// so the AI parts of the UI (who decided, prompt, confidence, cost, audit trail) can be seen locally.
/// Only the deterministic stub: no model is called and nothing costs money. Real model runs over
/// sandboxes belong to the scheduled job with its daily limits (phase 9).
/// </summary>
internal static class TenantRun
{
    public const string Usage = "Usage: match --tenant <id> [--mode stub]   (connection string ConnectionStrings:Payments from user-secrets --id payment-matching-ai)";

    public static async Task<int> RunAsync(string[] args)
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            named[args[i]] = args[i + 1];
        }

        if (!Guid.TryParse(named.GetValueOrDefault("--tenant"), out var tenantId) || named.Keys.Any(k => k is not ("--tenant" or "--mode")))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (named.GetValueOrDefault("--mode", "stub") != "stub")
        {
            Console.Error.WriteLine("Over an app sandbox only --mode stub is allowed (no model calls). Real runs come with the daily job.");
            return 2;
        }

        var config = new ConfigurationBuilder().AddUserSecrets("payment-matching-ai").AddEnvironmentVariables().Build();
        var connectionString = config.GetConnectionString(PaymentsDb.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Set ConnectionStrings:Payments (user-secrets --id payment-matching-ai or environment ConnectionStrings__Payments).");
            return 2;
        }

        await using var db = PaymentsDb.ForTenant(connectionString, tenantId);
        if (!await db.Tenants.AnyAsync())
        {
            Console.Error.WriteLine($"Sandbox {tenantId} does not exist.");
            return 1;
        }

        var report = await RunStubAsync(db, TimeProvider.System);
        Console.WriteLine($"Sandbox {tenantId}: {report.Records.Count} payments, {report.Records.Count - report.Reused} decided now, {report.Reused} already decided.");
        foreach (var group in report.Records.GroupBy(r => r.Decision.Outcome).OrderBy(g => g.Key))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }

        return 0;
    }

    /// <summary>The pipeline with the stub model over the tenant's data; decisions are saved under the tenant.</summary>
    public static async Task<RunReport> RunStubAsync(PaymentsDbContext db, TimeProvider time, CancellationToken cancellationToken = default)
    {
        var data = await TenantData.LoadDatasetAsync(db, cancellationToken);
        var store = await DbDecisionStore.LoadAsync(db, cancellationToken);
        var gateway = new PaymentMatching.AiGateway.AiGateway(new NaiveModelStub(), ModelSettings.Default, PaymentMatching.AiGateway.PriceTable.Claude);
        var report = await new MatchingPipeline(data, gateway, store, MatchingOptions.Default, time).RunAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return report;
    }
}
