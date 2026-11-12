using System.Net;
using PaymentMatching.AiGateway;
using PaymentMatching.Data;
using PaymentMatching.Matching;
using PaymentMatching.Testing;

namespace PaymentMatching.Web.Tests;

/// <summary>
/// The read-only demo and the admin overview: the two places where the tenant does not come from the
/// visitor's cookie (ADR 0013/0014). Also the AI budget on the dashboard.
/// </summary>
public class OperationsUiTests(PostgresFixture postgres)
{
    private static readonly Guid DemoId = Guid.Parse("00000000-0000-4000-8000-00000000d3e0");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task CreateDemoAsync(string connectionString) =>
        await new SandboxProvisioner(PaymentsDb.Options(connectionString), new SandboxOptions(), TimeProvider.System).EnsureDemoAsync(DemoId, Ct);

    private static Dictionary<string, string?> Demo(Guid id) => new() { ["Demo:TenantId"] = id.ToString() };

    [Fact]
    public async Task Demo_is_public_and_has_no_forms_or_actions()
    {
        var connectionString = await postgres.NewDatabaseAsync(Ct);
        await CreateDemoAsync(connectionString);
        await using var app = new WebApp(connectionString, settings: Demo(DemoId));
        var visitor = app.NewClient();

        var list = await visitor.GetStringAsync("/demo", Ct);
        var paymentId = new SandboxProvisioner(PaymentsDb.Options(connectionString), new SandboxOptions(), TimeProvider.System)
            .StarterData(SandboxProvisioner.DemoSeed, DateOnly.FromDateTime(DateTime.UtcNow)).Payments[0].Id;
        var detail = await visitor.GetStringAsync($"/demo/payments/{paymentId}", Ct);

        Assert.Contains("Demo sandbox (read-only)", list, StringComparison.Ordinal);
        Assert.Contains($"href=\"/demo/payments/{paymentId}\"", list, StringComparison.Ordinal);
        Assert.Contains("look at the demo sandbox", await visitor.GetStringAsync("/", Ct), StringComparison.Ordinal);
        foreach (var page in new[] { list, detail })
        {
            Assert.DoesNotContain("<form", page, StringComparison.Ordinal);
            Assert.DoesNotContain("/confirm", page, StringComparison.Ordinal);
            Assert.DoesNotContain("Sign out", page, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/demo/payments/NOPE", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/demo/payments/%3Cscript%3E", Ct)).StatusCode);

        // The person's actions do not reach the demo: without sign-in they are refused.
        var reject = await visitor.PostAsync($"/payments/{paymentId}/reject", new FormUrlEncodedContent([]), Ct);
        Assert.NotEqual(HttpStatusCode.Redirect, reject.StatusCode);
        Assert.Empty(await ReviewsAsync(connectionString, DemoId));
    }

    [Fact]
    public async Task A_signed_in_person_sees_the_same_demo_and_still_their_own_sandbox()
    {
        var connectionString = await postgres.NewDatabaseAsync(Ct);
        await CreateDemoAsync(connectionString);
        await using var app = new WebApp(connectionString, settings: Demo(DemoId));
        var alice = await app.SignedInAsAsync("alice");

        Assert.Equal(await app.NewClient().GetStringAsync("/demo", Ct), await alice.GetStringAsync("/demo", Ct));
        var me = await WebApp.MeAsync(alice);
        Assert.NotEqual(DemoId, me.TenantId);
        Assert.Contains("Your sandbox", await alice.GetStringAsync("/", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demo_setting_that_points_at_a_persons_sandbox_shows_nothing()
    {
        var connectionString = await postgres.NewDatabaseAsync(Ct);
        Guid aliceTenant;
        await using (var first = new WebApp(connectionString))
        {
            aliceTenant = (await WebApp.MeAsync(await first.SignedInAsAsync("alice"))).TenantId;
            Assert.Equal(HttpStatusCode.NotFound, (await first.NewClient().GetAsync("/demo", Ct)).StatusCode);
        }

        await using var misconfigured = new WebApp(connectionString, settings: Demo(aliceTenant));
        var visitor = misconfigured.NewClient();

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/demo", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/demo/payments/P0001", Ct)).StatusCode);
    }

    [Fact]
    public async Task Nothing_can_be_saved_through_the_demo_context()
    {
        var connectionString = await postgres.NewDatabaseAsync(Ct);
        await CreateDemoAsync(connectionString);
        PaymentsDbContext ReadOnly() => new(PaymentsDb.Options(connectionString), TenantContext.ReadOnlyFor(DemoId));

        await using (var db = ReadOnly())
        {
            var payment = db.Payments.First();
            payment.Message = "changed";
            await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync(Ct));
        }

        // Even the person's own action code cannot write through it.
        await using (var db = ReadOnly())
        {
            var state = await SandboxState.LoadAsync(db, null, Ct);
            var open = state.Data.Payments.First(p => state.IsActionable(p.Id));
            // The demo has no person (the action fails on that first); the read-only context would refuse the save anyway.
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new ReviewActions(db, TimeProvider.System).RejectAsync(open.Id, Ct));
        }

        Assert.Empty(await ReviewsAsync(connectionString, DemoId));
    }

    [Fact]
    public async Task Admin_is_not_found_except_for_listed_accounts_and_shows_aggregates_only()
    {
        var connectionString = await postgres.NewDatabaseAsync(Ct);
        await using var app = new WebApp(connectionString, settings: new Dictionary<string, string?> { ["Admin:Accounts:0"] = "dev:boss" });
        var alice = await app.SignedInAsAsync("alice");
        var aliceMe = await WebApp.MeAsync(alice);
        var boss = await app.SignedInAsAsync("boss");
        await new UsageLedger(OperationsDb.Options(connectionString)).AddAsync(DateOnly.FromDateTime(DateTime.UtcNow), new UsageDelta(AiCalls: 3, AiCostUsd: 0.0024m, CacheHits: 9), Ct);

        Assert.Equal(HttpStatusCode.NotFound, (await app.NewClient().GetAsync("/admin", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/admin", Ct)).StatusCode);

        var page = await boss.GetStringAsync("/admin", Ct);
        Assert.Contains("Operations overview", page, StringComparison.Ordinal);
        Assert.Contains("75.0 %", page, StringComparison.Ordinal);
        Assert.Contains("<td class=\"num\">2</td>", page, StringComparison.Ordinal);
        foreach (var personal in new[] { "alice", "boss", aliceMe.Email, aliceMe.TenantId.ToString(), "P0001", "2026-1001" })
        {
            Assert.DoesNotContain(personal, page, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Dashboard_shows_the_ai_budget_and_payments_label_a_used_up_budget()
    {
        var connectionString = await postgres.NewDatabaseAsync(Ct);
        await using var app = new WebApp(connectionString, settings: new Dictionary<string, string?> { ["Ai:Budget:CallsPerDay"] = "5" });
        var alice = await app.SignedInAsAsync("alice");
        var tenant = (await WebApp.MeAsync(alice)).TenantId;
        var labelled = await DecideWaitingPaymentAsync(connectionString, tenant);
        await new UsageLedger(OperationsDb.Options(connectionString)).AddAsync(DateOnly.FromDateTime(DateTime.UtcNow), new UsageDelta(AiCalls: 5), Ct);

        var dashboard = await alice.GetStringAsync("/", Ct);
        Assert.Contains("used up for today", dashboard, StringComparison.Ordinal);
        Assert.Contains("0 / 2", dashboard, StringComparison.Ordinal);
        Assert.Contains("1 waiting for you", dashboard, StringComparison.Ordinal);
        Assert.Contains("AI budget used up", await alice.GetStringAsync("/queue", Ct), StringComparison.Ordinal);
        Assert.Contains("AI budget used up", await alice.GetStringAsync($"/payments/{labelled}", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turned_away_sign_ins_are_counted_for_the_waiting_list()
    {
        var connectionString = await postgres.NewDatabaseAsync(Ct);
        await using var app = new WebApp(connectionString, settings: new Dictionary<string, string?> { ["Sandboxes:MaxNewPerDay"] = "0" });

        await app.NewClient().GetAsync("/dev-login?name=alice", Ct);
        await app.NewClient().GetAsync("/dev-login?name=bob", Ct);

        var today = await new UsageLedger(OperationsDb.Options(connectionString)).DayAsync(DateOnly.FromDateTime(DateTime.UtcNow), Ct);
        Assert.Equal(2, today.SignInsTurnedAway);
    }

    /// <summary>Decides one payment that waits for the AI run as the job does when the app budget is used up.</summary>
    private static async Task<string> DecideWaitingPaymentAsync(string connectionString, Guid tenant)
    {
        await using var db = PaymentsDb.ForTenant(connectionString, tenant);
        var state = await SandboxState.LoadAsync(db, null, Ct);
        var payment = state.Data.Payments.First(p => state.StateOf(p.Id) == PaymentState.WaitingForAi);
        var store = await DbDecisionStore.LoadAsync(db, Ct);
        var result = new GatewayResult<AiMatchAnswer>(null, null, [], false, BudgetExhausted: true);
        var decision = new AiDecisionMaker(MatchingOptions.Default).Decide(payment, result, null, payerIsKnownCustomer: true, injectionDetected: false);
        store.Append(new AuditRecord(DecisionLog.KeyFor(payment), payment.Id, DateTimeOffset.UtcNow, AuditRecord.AiLayer, "-", PromptBuilder.Version, decision, [], [], []));
        await db.SaveChangesAsync(Ct);
        return payment.Id;
    }

    private static async Task<List<ReviewRow>> ReviewsAsync(string connectionString, Guid tenant)
    {
        await using var db = PaymentsDb.ForTenant(connectionString, tenant);
        return [.. db.Reviews];
    }
}
