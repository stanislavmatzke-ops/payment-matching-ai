using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Matching;
using PaymentMatching.Testing;

namespace PaymentMatching.Job.Tests;

/// <summary>Who gets the daily batch, idempotency, clean-up and the demo sandbox.</summary>
public class DailyJobTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Active_sandbox_gets_the_batch_and_its_ai_payments_are_decided()
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        var sandbox = await bed.SandboxAsync();
        var model = new CountingModel();

        var report = await bed.Job(model).RunAsync(Ct);

        var run = Assert.Single(report.Sandboxes);
        Assert.True(run.Refilled);
        var batch = DailyBatch.For(bed.Today);
        var states = await bed.StatesAsync(sandbox);
        Assert.Equal(batch.Data.Payments.Select(p => p.Id).Order(), states.Keys.Order());
        Assert.DoesNotContain(PaymentState.WaitingForAi, states.Values);
        Assert.InRange(model.Requests.Count, 0, 2);
        Assert.Equal(model.Requests.Count, (await bed.Ledger.DayAsync(bed.Today, Ct)).AiCalls);
    }

    [Fact]
    public async Task Inactive_sandbox_gets_nothing_and_costs_nothing()
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        var idle = await bed.SandboxAsync(seed: 11, lastActiveAgo: TimeSpan.FromDays(8));
        var before = await bed.StatesAsync(idle);
        var model = new CountingModel();

        var report = await bed.Job(model).RunAsync(Ct);

        Assert.Empty(report.Sandboxes);
        Assert.Empty(model.Requests);
        Assert.Equal(before, await bed.StatesAsync(idle));
    }

    [Fact]
    public async Task Second_run_on_the_same_day_adds_nothing_and_asks_no_model()
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        var sandbox = await bed.SandboxAsync();
        var model = new CountingModel();
        await bed.Job(model).RunAsync(Ct);
        var calls = model.Requests.Count;
        var states = await bed.StatesAsync(sandbox);

        bed.Time.Now = bed.Time.Now.AddHours(3);
        var again = await bed.Job(model).RunAsync(Ct);

        Assert.False(Assert.Single(again.Sandboxes).Refilled);
        Assert.Equal(calls, model.Requests.Count);
        Assert.Equal(states, await bed.StatesAsync(sandbox));
    }

    [Fact]
    public async Task Full_queue_gets_no_batch_until_the_person_resolves_something()
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        var sandbox = await bed.SandboxAsync();
        var model = new CountingModel();

        // Day 1: empty queue, so the batch comes. Fill the queue to the threshold by hand.
        await bed.Job(model).RunAsync(Ct);
        await using (var db = bed.For(sandbox))
        {
            var data = new SandboxProvisioner(bed.Payments, new SandboxOptions(), bed.Time).StarterData(5, bed.Today);
            var extra = data.Payments.Take(3).Select((p, i) => p with { Id = $"QX{i}", RawLine = p.RawLine + $";QX{i}" }).ToList();
            TenantData.Add(db, new MatchingDataset(data.Customers.Take(0).ToList(), [], extra), 1000);
            var store = await DbDecisionStore.LoadAsync(db, Ct);
            foreach (var p in extra)
            {
                store.Append(new AuditRecord(DecisionLog.KeyFor(p), p.Id, JobTestBed.Now, AuditRecord.PolicyLayer, "-", null,
                    new Decision(p.Id, Outcome.HumanReview, [], DecidedBy.Policy, "test", []), [], [], []));
            }

            await db.SaveChangesAsync(Ct);
            Assert.True((await QueueAsync(db)).Count >= 3);
        }

        // Day 2: the queue is full and nothing was resolved: no batch.
        bed.Time.Now = bed.Time.Now.AddDays(1);
        Assert.False(Assert.Single((await bed.Job(model).RunAsync(Ct)).Sandboxes).Refilled);

        // The person resolves one payment: day 3 brings the batch again.
        await using (var db = bed.For(sandbox))
        {
            var result = await new ReviewActions(db, bed.Time).RejectAsync("QX0", Ct);
            Assert.Equal(ReviewOutcome.Done, result.Outcome);
        }

        bed.Time.Now = bed.Time.Now.AddDays(1);
        Assert.True(Assert.Single((await bed.Job(model).RunAsync(Ct)).Sandboxes).Refilled);
    }

    [Fact]
    public async Task Demo_is_created_once_without_a_user_refilled_every_day_and_never_deleted()
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        var demo = Guid.NewGuid();
        var model = new CountingModel();

        var first = await bed.Job(model, demo: demo).RunAsync(Ct);
        Assert.True(first.DemoCreated);
        await using (var db = bed.For(demo))
        {
            var tenant = await db.Tenants.SingleAsync(Ct);
            Assert.True(tenant.IsDemo);
            Assert.Empty(await db.Users.ToListAsync(Ct));
        }

        // Nobody ever visits the demo while signed in; 40 days later it is still there and still refilled.
        for (var day = 1; day <= 40; day += 13)
        {
            bed.Time.Now = JobTestBed.Now.AddDays(day);
            var report = await bed.Job(model, demo: demo).RunAsync(Ct);
            Assert.False(report.DemoCreated);
            Assert.True(Assert.Single(report.Sandboxes, s => s.TenantId == demo).Refilled);
        }

        var states = await bed.StatesAsync(demo);
        Assert.Contains(states.Keys, id => id.StartsWith("P261112-", StringComparison.Ordinal));
        Assert.Contains(states.Keys, id => id.StartsWith(DateOnly.FromDateTime(bed.Time.Now.UtcDateTime).ToString("'P'yyMMdd'-'", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inactive_sandboxes_are_deleted_by_the_job_and_counted()
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        var old = await bed.SandboxAsync(seed: 3, lastActiveAgo: TimeSpan.FromDays(31));
        var kept = await bed.SandboxAsync(seed: 4, lastActiveAgo: TimeSpan.FromDays(20));

        var report = await bed.Job(new CountingModel()).RunAsync(Ct);

        Assert.Equal(1, report.Deleted);
        await using (var db = bed.For(old))
        {
            Assert.False(await db.Tenants.AnyAsync(Ct));
            Assert.False(await db.Payments.AnyAsync(Ct));
        }

        await using (var db = bed.For(kept))
        {
            Assert.True(await db.Tenants.AnyAsync(Ct));
        }

        Assert.Equal(1, (await bed.Ledger.DayAsync(bed.Today, Ct)).SandboxesDeleted);
    }

    private static async Task<List<string>> QueueAsync(PaymentsDbContext db)
    {
        var state = await SandboxState.LoadAsync(db, null, Ct);
        return state.Data.Payments.Where(p => state.StateOf(p.Id) == PaymentState.NeedsReview).Select(p => p.Id).ToList();
    }
}
