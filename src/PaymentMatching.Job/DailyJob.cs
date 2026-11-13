using System.Diagnostics;
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

    /// <summary>Decide the week's starter set first, so sign-ins get AI decisions from the cache (ADR 0017).</summary>
    public bool PrecomputeStarter { get; init; } = true;
}

/// <summary>What one sandbox got in this run.</summary>
public sealed record SandboxRun(Guid TenantId, bool Refilled, int Decided, int ModelCalls, int CacheHits, decimal CostUsd, int WaitingForAi, BudgetLimit LimitedBy);

/// <summary>The week's starter set, decided in memory to fill the response cache for sign-ins (ADR 0017).</summary>
public sealed record StarterRun(DateOnly Week, int ModelCalls, int CacheHits, decimal CostUsd, int WaitingForAi);

public sealed record JobReport(DateOnly Day, int Deleted, bool DemoCreated, IReadOnlyList<SandboxRun> Sandboxes, int Failed, StarterRun? Starter = null)
{
    public int ModelCalls => Sandboxes.Sum(s => s.ModelCalls) + (Starter?.ModelCalls ?? 0);

    public int CacheHits => Sandboxes.Sum(s => s.CacheHits) + (Starter?.CacheHits ?? 0);

    public decimal CostUsd => Sandboxes.Sum(s => s.CostUsd) + (Starter?.CostUsd ?? 0);
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
    private readonly SandboxProvisioner _provisioner = new(payments, new SandboxOptions(), time);
    private readonly TextWriter _log = log ?? TextWriter.Null;

    public async Task<JobReport> RunAsync(CancellationToken cancellationToken = default)
    {
        using var activity = Telemetry.Source.StartActivity("job.run");
        var report = await RunAllAsync(cancellationToken);
        activity?.SetTag(Tags.Day, report.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .SetTag(Tags.Mode, settings.Source)
            .SetTag(Tags.Deleted, report.Deleted)
            .SetTag(Tags.DemoCreated, report.DemoCreated)
            .SetTag(Tags.Sandboxes, report.Sandboxes.Count)
            .SetTag(Tags.Refilled, report.Sandboxes.Count(s => s.Refilled))
            .SetTag(Telemetry.Tags.AiCalls, report.ModelCalls)
            .SetTag(Tags.CacheHits, report.CacheHits)
            .SetTag(Telemetry.Tags.AiCostUsd, (double)report.CostUsd)
            .SetTag(Tags.Failed, report.Failed);
        if (report.Failed > 0)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "sandbox failed");
        }

        return report;
    }

    /// <summary>Tag names of the job spans: day, counts, codes and a tenant pseudonym (ADR 0015).</summary>
    public static class Tags
    {
        public const string Day = "payments.job.day";
        public const string Mode = "payments.job.mode";
        public const string Deleted = "payments.job.deleted";
        public const string DemoCreated = "payments.job.demo_created";
        public const string Sandboxes = "payments.job.sandboxes";
        public const string Refilled = "payments.job.refilled";
        public const string CacheHits = "payments.job.cache_hits";
        public const string Failed = "payments.job.failed";
        public const string Tenant = "payments.tenant.hash";
        public const string Decided = "payments.job.decided";
        public const string Waiting = "payments.job.waiting_for_ai";
        public const string Limit = "payments.job.limited_by";
    }

    private async Task<JobReport> RunAllAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var deleted = await _admin.DeleteInactiveAsync(now, cancellationToken);
        if (deleted > 0)
        {
            await _ledger.AddAsync(today, new UsageDelta(SandboxesDeleted: deleted), cancellationToken);
        }

        var demoCreated = settings.DemoTenantId is { } demo
            && await _provisioner.EnsureDemoAsync(demo, cancellationToken);

        var batch = DailyBatch.For(today);
        var cache = new CachingProvider(operations, settings.Source, model, time);
        StarterRun? starter = null;
        if (settings.PrecomputeStarter)
        {
            starter = await PrecomputeStarterAsync(today, cache, cancellationToken);
            _log.WriteLine(FormattableString.Invariant($"starter set {starter.Week:yyyy-MM-dd}: calls={starter.ModelCalls} cached={starter.CacheHits} cost=${starter.CostUsd:0.0000} waiting={starter.WaitingForAi}"));
        }

        var runs = new List<SandboxRun>();
        var failed = 0;
        foreach (var tenantId in await _admin.ActiveTenantIdsAsync(now, cancellationToken))
        {
            using var span = Telemetry.Source.StartActivity("job.sandbox");
            span?.SetTag(Tags.Tenant, Telemetry.TenantHash(tenantId));
            try
            {
                var run = await RunSandboxAsync(tenantId, batch, cache, now, cancellationToken);
                runs.Add(run);
                span?.SetTag(Tags.Refilled, run.Refilled)
                    .SetTag(Tags.Decided, run.Decided)
                    .SetTag(Telemetry.Tags.AiCalls, run.ModelCalls)
                    .SetTag(Tags.CacheHits, run.CacheHits)
                    .SetTag(Telemetry.Tags.AiCostUsd, (double)run.CostUsd)
                    .SetTag(Tags.Waiting, run.WaitingForAi)
                    .SetTag(Tags.Limit, run.LimitedBy.ToString());
                _log.WriteLine(FormattableString.Invariant($"{tenantId}: refilled={run.Refilled} decided={run.Decided} calls={run.ModelCalls} cached={run.CacheHits} cost=${run.CostUsd:0.0000} waiting={run.WaitingForAi} limit={run.LimitedBy}"));
            }
            catch (Exception ex) when (ex is DbUpdateException or TenantIsolationException or InvalidOperationException)
            {
                // One sandbox must not stop the others (e.g. a person matched the same invoice at the same moment).
                failed++;
                span?.SetTag(Telemetry.Tags.ErrorType, ex.GetType().Name).SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                _log.WriteLine($"{tenantId}: failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new JobReport(today, deleted, demoCreated, runs, failed, starter);
    }

    /// <summary>
    /// Decides the week's starter set in memory, exactly as a sign-in does, with the model behind the cache and
    /// within the app's day and month budgets. The first run of a week asks the model (~5–7 calls); every later
    /// run of that week finds all answers cached and costs nothing. Sign-ins of the week then reuse them.
    /// </summary>
    private async Task<StarterRun> PrecomputeStarterAsync(DateOnly today, CachingProvider cache, CancellationToken cancellationToken)
    {
        var week = StarterSet.WeekOf(today);
        var data = _provisioner.StarterData(StarterSet.SeedOf(week), week);
        var allowance = AiAllowance.For(
            settings.Budget,
            sandboxCallsToday: 0,
            await _ledger.DayAsync(today, cancellationToken),
            await _ledger.MonthAsync(today, cancellationToken));
        var gateway = Gateway(cache, allowance);
        var decisions = new DecisionLog();
        await _provisioner.DecideStarterAsync(data, decisions, gateway, cancellationToken);

        var usage = gateway.Usage;
        await _ledger.AddAsync(today, new UsageDelta(usage.ModelCalls, usage.CostUsd, usage.CacheHits), cancellationToken);
        return new StarterRun(week, usage.ModelCalls, usage.CacheHits, usage.CostUsd, data.Payments.Count - decisions.Records.Count);
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

        var gateway = Gateway(cache, allowance);
        var options = MatchingOptions.Default with { DeferAiWhenBudgetExhausted = allowance.LimitedBy == BudgetLimit.Sandbox };
        var report = await new MatchingPipeline(data, gateway, store, options, time).RunAsync(cancellationToken);

        // The calls happened whether or not the save below succeeds, so they are counted first.
        var usage = gateway.Usage;
        await _ledger.AddAsync(today, new UsageDelta(usage.ModelCalls, usage.CostUsd, usage.CacheHits, SandboxesRefilled: refilled ? 1 : 0), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return new SandboxRun(
            tenantId,
            refilled,
            report.Records.Count - report.Reused,
            usage.ModelCalls,
            usage.CacheHits,
            usage.CostUsd,
            data.Payments.Count - report.Records.Count,
            allowance.LimitedBy);
    }

    /// <summary>The same routes everywhere (the cache key contains the model), capped by today's allowance.</summary>
    private AiGateway.AiGateway Gateway(CachingProvider cache, AiAllowance allowance) =>
        new(cache, StarterSet.Routes(settings.Budget.SmallModelOnly) with { MaxCalls = allowance.Calls }, PriceTable.Claude);

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
