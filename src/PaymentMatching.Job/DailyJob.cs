using Microsoft.EntityFrameworkCore;
using PaymentMatching.AiGateway;
using PaymentMatching.Data;
using PaymentMatching.Matching;

namespace PaymentMatching.Job;

/// <summary>Settings of one job run.</summary>
/// <param name="Source">Who answers: "live" (a model, costs money) or "stub" (no key, no cost). Cached answers never cross sources.</param>
/// <param name="DemoTenantId">The read-only demo sandbox (server configuration); null = no demo.</param>
public sealed record JobSettings(string Source, Guid? DemoTenantId, AiBudgetOptions Budget)
{
    /// <summary>A sandbox gets the day's batch when fewer than this many payments wait for the person.</summary>
    public int QueueBelow { get; init; } = 3;
}

/// <summary>What one sandbox got in this run.</summary>
public sealed record SandboxRun(Guid TenantId, bool Refilled, int Decided, int ModelCalls, int CacheHits, decimal CostUsd, int WaitingForAi, BudgetLimit LimitedBy);

public sealed record JobReport(DateOnly Day, int Deleted, bool DemoCreated, IReadOnlyList<SandboxRun> Sandboxes, int Failed)
{
    public int ModelCalls => Sandboxes.Sum(s => s.ModelCalls);

    public int CacheHits => Sandboxes.Sum(s => s.CacheHits);

    public decimal CostUsd => Sandboxes.Sum(s => s.CostUsd);
}

/// <summary>
/// The scheduled daily job (SPEC 9b, ADR 0013), one run then exit:
/// <list type="number">
/// <item>delete sandboxes unused for 30 days (never the demo),</item>
/// <item>make sure the demo sandbox exists,</item>
/// <item>for each sandbox used in the last 7 days and the demo: add the shared daily batch when the person
/// resolved something since the last batch or the queue is short (the demo always), then run rules → AI →
/// validation over every undecided payment within the AI budgets.</item>
/// </list>
/// Every sandbox is handled in its own tenant context; only <see cref="TenantAdmin"/> sees across them,
/// and only ids. Running twice a day adds nothing and asks no model twice (idempotent).
/// </summary>
public sealed class DailyJob(
    DbContextOptions<PaymentsDbContext> payments,
    DbContextOptions<OperationsDbContext> operations,
    ILlmProvider model,
    JobSettings settings,
    TimeProvider time,
    TextWriter? log = null)
{
    private readonly TenantAdmin _admin = new(payments);
    private readonly UsageLedger _ledger = new(operations);
    private readonly TextWriter _log = log ?? TextWriter.Null;

    public async Task<JobReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var deleted = await _admin.DeleteInactiveAsync(now, cancellationToken);
        if (deleted > 0)
        {
            await _ledger.AddAsync(today, new UsageDelta(SandboxesDeleted: deleted), cancellationToken);
        }

        var demoCreated = settings.DemoTenantId is { } demo
            && await new SandboxProvisioner(payments, new SandboxOptions(), time).EnsureDemoAsync(demo, cancellationToken);

        var batch = DailyBatch.For(today);
        var cache = new CachingProvider(operations, settings.Source, model, time);
        var runs = new List<SandboxRun>();
        var failed = 0;
        foreach (var tenantId in await _admin.ActiveTenantIdsAsync(now, cancellationToken))
        {
            try
            {
                var run = await RunSandboxAsync(tenantId, batch, cache, now, cancellationToken);
                runs.Add(run);
                _log.WriteLine($"{tenantId}: refilled={run.Refilled} decided={run.Decided} calls={run.ModelCalls} cached={run.CacheHits} cost=${run.CostUsd:0.0000} waiting={run.WaitingForAi} limit={run.LimitedBy}");
            }
            catch (Exception ex) when (ex is DbUpdateException or TenantIsolationException or InvalidOperationException)
            {
                // One sandbox must not stop the others (e.g. a person matched the same invoice at the same moment).
                failed++;
                _log.WriteLine($"{tenantId}: failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new JobReport(today, deleted, demoCreated, runs, failed);
    }

    private async Task<SandboxRun> RunSandboxAsync(Guid tenantId, DailyBatch batch, CachingProvider cache, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var today = batch.Day;
        await using var db = new PaymentsDbContext(payments, new TenantContext(tenantId));
        var tenant = await db.Tenants.SingleAsync(cancellationToken);

        var refilled = false;
        if (tenant.LastBatchDate != today && await NeedsRefillAsync(db, tenant, cancellationToken))
        {
            var nextSeq = (await db.Payments.MaxAsync(p => (int?)p.Seq, cancellationToken) ?? -1) + 1;
            TenantData.Add(db, batch.Data, nextSeq);
            tenant.LastBatchDate = today;
            tenant.LastRefillAt = now;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            refilled = true;
        }

        var data = await TenantData.LoadDatasetAsync(db, cancellationToken);
        var store = await DbDecisionStore.LoadAsync(db, cancellationToken);
        var callsToday = store.Records
            .Where(r => DateOnly.FromDateTime(r.At.UtcDateTime) == today)
            .SelectMany(r => r.Calls)
            .Count(c => !c.Cached);
        var allowance = AiAllowance.For(
            settings.Budget,
            callsToday,
            await _ledger.DayAsync(today, cancellationToken),
            await _ledger.MonthAsync(today, cancellationToken));

        var routes = settings.Budget.SmallModelOnly ? ModelSettings.Default with { Large = null } : ModelSettings.Default;
        var gateway = new AiGateway.AiGateway(cache, routes with { MaxCalls = allowance.Calls }, PriceTable.Claude);
        var options = MatchingOptions.Default with { DeferAiWhenBudgetExhausted = allowance.LimitedBy == BudgetLimit.Sandbox };
        var report = await new MatchingPipeline(data, gateway, store, options, time).RunAsync(cancellationToken);

        // The calls happened whether or not the save below succeeds, so they are counted first.
        var modelCalls = gateway.Calls.Count(c => !c.Cached);
        var cacheHits = gateway.Calls.Count(c => c.Cached);
        var cost = gateway.Calls.Sum(c => c.CostUsd);
        await _ledger.AddAsync(today, new UsageDelta(modelCalls, cost, cacheHits, SandboxesRefilled: refilled ? 1 : 0), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return new SandboxRun(
            tenantId,
            refilled,
            report.Records.Count - report.Reused,
            modelCalls,
            cacheHits,
            cost,
            data.Payments.Count - report.Records.Count,
            allowance.LimitedBy);
    }

    /// <summary>The demo always; others when the person resolved something since the last batch or the queue is short.</summary>
    private async Task<bool> NeedsRefillAsync(PaymentsDbContext db, TenantRow tenant, CancellationToken cancellationToken)
    {
        if (tenant.IsDemo)
        {
            return true;
        }

        var since = tenant.LastRefillAt ?? DateTimeOffset.UnixEpoch;
        if (await db.Reviews.AnyAsync(r => r.At > since, cancellationToken))
        {
            return true;
        }

        var state = await SandboxState.LoadAsync(db, null, cancellationToken);
        return state.Data.Payments.Count(p => state.StateOf(p.Id) == PaymentState.NeedsReview) < settings.QueueBelow;
    }
}
