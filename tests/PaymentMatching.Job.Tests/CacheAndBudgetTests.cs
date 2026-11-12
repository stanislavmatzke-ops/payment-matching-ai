using PaymentMatching.AiGateway;
using PaymentMatching.Data;
using PaymentMatching.DataGen.Model;
using PaymentMatching.Matching;
using PaymentMatching.Testing;

namespace PaymentMatching.Job.Tests;

/// <summary>The response cache and the three budget layers of ADR 0013, end to end through the job.</summary>
public class CacheAndBudgetTests(PostgresFixture postgres)
{
    private static readonly CaseType[] Asked = [CaseType.UnparsedReference, CaseType.VsTypo, CaseType.MultiInvoice];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JobTestBed> BedAsync(PostgresFixture postgres)
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        bed.MoveToDayWithTwoModelCases();
        return bed;
    }

    private static List<string> ModelCases(JobTestBed bed) =>
        DailyBatch.For(bed.Today).Generated.Expected.Where(e => Asked.Contains(e.CaseType)).Select(e => e.PaymentId).ToList();

    private static async Task<Dictionary<string, AuditRecord>> AiDecisionsAsync(JobTestBed bed, Guid sandbox) =>
        (await bed.DecisionsAsync(sandbox)).Where(r => r.Layer == AuditRecord.AiLayer).ToDictionary(r => r.PaymentId);

    [Fact]
    public async Task Second_sandbox_reuses_the_cached_answers_without_a_call_and_decides_the_same()
    {
        var bed = await BedAsync(postgres);
        var a = await bed.SandboxAsync();
        var b = await bed.SandboxAsync();
        var model = new CountingModel();

        var report = await bed.Job(model).RunAsync(Ct);

        Assert.Equal(2, model.Requests.Count);
        var second = report.Sandboxes.Single(s => s.TenantId == b);
        Assert.Equal(0, second.ModelCalls);
        Assert.Equal(2, second.CacheHits);
        Assert.Equal(0m, second.CostUsd);

        var first = await AiDecisionsAsync(bed, a);
        var reused = await AiDecisionsAsync(bed, b);
        Assert.Equal(ModelCases(bed).Order(), reused.Keys.Order());
        Assert.All(reused, r =>
        {
            Assert.Equal(first[r.Key].Decision.Outcome, r.Value.Decision.Outcome);
            Assert.Equal(first[r.Key].Decision.InvoiceNumbers, r.Value.Decision.InvoiceNumbers);
            Assert.True(Assert.Single(r.Value.Calls).Cached);
        });

        var day = await bed.Ledger.DayAsync(bed.Today, Ct);
        Assert.Equal((2, 2), (day.AiCalls, day.CacheHits));
        Assert.Equal(0.5, day.CacheHitRate);
    }

    [Fact]
    public async Task A_cached_answer_is_validated_again_in_every_sandbox()
    {
        var bed = await BedAsync(postgres);
        var a = await bed.SandboxAsync();
        var b = await bed.SandboxAsync();

        // A model that names an invoice no code ever offered: validation must stop it, cached or not.
        await bed.Job(new CountingModel(_ => CountingModel.WrongInvoice("2026-1001"))).RunAsync(Ct);

        foreach (var sandbox in new[] { a, b })
        {
            var decisions = await AiDecisionsAsync(bed, sandbox);
            Assert.NotEmpty(decisions);
            Assert.All(decisions.Values, d =>
            {
                Assert.NotEqual(Outcome.AutoMatched, d.Decision.Outcome);
                Assert.Contains(Rejections.NotACandidate, d.ValidationFailures);
            });
        }

        Assert.All((await AiDecisionsAsync(bed, b)).Values, d => Assert.True(d.Calls.Single().Cached));
    }

    [Fact]
    public async Task A_sandbox_whose_invoices_differ_sends_a_different_prompt_and_the_model_is_asked()
    {
        var bed = await BedAsync(postgres);
        await bed.SandboxAsync();
        var model = new CountingModel();
        await bed.Job(model).RunAsync(Ct);
        Assert.Equal(2, model.Requests.Count);

        // Another sandbox has one more open invoice of the same customer and amount: a new candidate.
        var batch = DailyBatch.For(bed.Today);
        var expected = batch.Generated.Expected.First(e => Asked.Contains(e.CaseType));
        var invoice = batch.Data.InvoiceByNumber[expected.InvoiceNumbers[0]];
        var other = await bed.SandboxAsync();
        await using (var db = bed.For(other))
        {
            db.Invoices.Add(new InvoiceRow
            {
                Number = "2026-999999",
                VariableSymbol = "2026999999",
                CustomerId = invoice.CustomerId,
                Kind = invoice.Kind,
                DueDate = invoice.DueDate,
                Amount = invoice.Amount,
                Currency = invoice.Currency,
            });
            await db.SaveChangesAsync(Ct);
        }

        bed.Time.Now = bed.Time.Now.AddHours(1);
        var report = await bed.Job(model).RunAsync(Ct);

        Assert.True(report.Sandboxes.Single(s => s.TenantId == other).ModelCalls >= 1);
        Assert.Contains(model.Requests.Skip(2), r => r.UserContent.Contains("2026-999999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Answers_of_the_stub_never_serve_a_live_run()
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        var request = new LlmRequest("claude-haiku-5-5", "low", 100, "system", "user", "{}");
        var stub = new CountingModel(_ => CountingModel.WrongInvoice("stub"));
        var live = new CountingModel(_ => CountingModel.WrongInvoice("live"));

        await new CachingProvider(bed.Operations, "stub", stub, bed.Time).CompleteJsonAsync(request, Ct);
        var fromStubCache = await new CachingProvider(bed.Operations, "stub", stub, bed.Time).CompleteJsonAsync(request, Ct);
        var fromLive = await new CachingProvider(bed.Operations, "live", live, bed.Time).CompleteJsonAsync(request, Ct);

        Assert.True(fromStubCache.Cached);
        Assert.False(fromLive.Cached);
        Assert.Single(stub.Requests);
        Assert.Single(live.Requests);
    }

    [Fact]
    public async Task Over_the_sandbox_limit_the_rest_waits_for_the_next_run_without_a_label()
    {
        var bed = await BedAsync(postgres);
        var sandbox = await bed.SandboxAsync();
        var model = new CountingModel();
        var budget = AiBudgetOptions.Default with { CallsPerSandboxPerDay = 1 };

        var report = await bed.Job(model, budget).RunAsync(Ct);

        Assert.Single(model.Requests);
        Assert.Equal(BudgetLimit.Sandbox, Assert.Single(report.Sandboxes).LimitedBy);
        var states = await bed.StatesAsync(sandbox);
        var waiting = Assert.Single(ModelCases(bed), id => states[id] == PaymentState.WaitingForAi);
        Assert.DoesNotContain(await bed.DecisionsAsync(sandbox), d => d.Decision.Flags.Contains(Flags.AiBudgetUsedUp));

        // Next day the waiting payment gets its call.
        bed.Time.Now = bed.Time.Now.AddDays(1);
        await bed.Job(model, budget).RunAsync(Ct);
        Assert.NotEqual(PaymentState.WaitingForAi, (await bed.StatesAsync(sandbox))[waiting]);
    }

    [Theory]
    [InlineData("day")]
    [InlineData("month")]
    public async Task When_the_app_budget_is_used_up_ai_payments_go_to_a_person_with_a_label(string layer)
    {
        var bed = await BedAsync(postgres);
        var sandbox = await bed.SandboxAsync();
        var used = layer == "day" ? new UsageDelta(AiCalls: 50) : new UsageDelta(AiCalls: 3, AiCostUsd: 1.98m);
        await bed.Ledger.AddAsync(bed.Today, used, Ct);
        var model = new CountingModel();

        var report = await bed.Job(model).RunAsync(Ct);

        Assert.Empty(model.Requests);
        Assert.Equal(layer == "day" ? BudgetLimit.Day : BudgetLimit.Month, Assert.Single(report.Sandboxes).LimitedBy);
        var decisions = await AiDecisionsAsync(bed, sandbox);
        Assert.All(ModelCases(bed), id =>
        {
            Assert.Equal(Outcome.HumanReview, decisions[id].Decision.Outcome);
            Assert.Contains(Flags.AiBudgetUsedUp, decisions[id].Decision.Flags);
            Assert.Empty(decisions[id].Calls);
        });
    }

    [Fact]
    public async Task Cached_answers_are_served_even_when_the_sandbox_has_no_calls_left()
    {
        var bed = await BedAsync(postgres);
        await bed.SandboxAsync();
        var model = new CountingModel();
        await bed.Job(model).RunAsync(Ct);

        var late = await bed.SandboxAsync();
        bed.Time.Now = bed.Time.Now.AddHours(1);
        var report = await bed.Job(model, AiBudgetOptions.Default with { CallsPerSandboxPerDay = 0 }).RunAsync(Ct);

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, report.Sandboxes.Single(s => s.TenantId == late).CacheHits);
        var states = await bed.StatesAsync(late);
        Assert.All(ModelCases(bed), id => Assert.NotEqual(PaymentState.WaitingForAi, states[id]));
    }

    [Fact]
    public async Task Small_model_only_never_asks_the_large_model()
    {
        var bed = await BedAsync(postgres);
        await bed.SandboxAsync();
        var unsure = new CountingModel(r => CountingModel.WrongInvoice("2026-1001").Replace("0.99", "0.5", StringComparison.Ordinal));

        await bed.Job(unsure, AiBudgetOptions.Default with { SmallModelOnly = true, CallsPerSandboxPerDay = 10 }).RunAsync(Ct);

        Assert.NotEmpty(unsure.Requests);
        Assert.All(unsure.Requests, r => Assert.Equal(ModelSettings.Default.Small.Model, r.Model));
    }

    [Theory]
    [InlineData(0, 0, 0, 2, BudgetLimit.Sandbox)]
    [InlineData(1, 0, 0, 1, BudgetLimit.Sandbox)]
    [InlineData(2, 0, 0, 0, BudgetLimit.Sandbox)]
    [InlineData(0, 49, 0, 1, BudgetLimit.Day)]
    [InlineData(0, 48, 0, 2, BudgetLimit.Day)]
    [InlineData(0, 0, 1.95, 1, BudgetLimit.Month)]
    [InlineData(0, 0, 1.96, 0, BudgetLimit.Month)]
    [InlineData(0, 50, 2.5, 0, BudgetLimit.Month)]
    public void Allowance_is_the_tightest_layer_and_a_tie_names_the_app(int sandboxCalls, int appCalls, double monthUsd, int calls, BudgetLimit limit)
    {
        var allowance = AiAllowance.For(
            AiBudgetOptions.Default with { CallsPerSandboxPerDay = 2 },
            sandboxCalls,
            UsageTotals.Zero with { AiCalls = appCalls },
            UsageTotals.Zero with { AiCostUsd = (decimal)monthUsd });

        Assert.Equal(new AiAllowance(calls, limit), allowance);
    }

    [Fact]
    public void Budget_settings_are_read_with_safe_defaults()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Budget:CallsPerSandboxPerDay"] = "5",
            ["Ai:Budget:MonthlyUsd"] = "0.5",
            ["Ai:Budget:CallsPerDay"] = "-3",
            ["Ai:Budget:ReservePerCallUsd"] = "0",
            ["Ai:SmallModelOnly"] = "true",
        };

        var options = AiBudgetOptions.Read(k => settings.GetValueOrDefault(k));

        Assert.Equal(new AiBudgetOptions { CallsPerSandboxPerDay = 5, MonthlyUsd = 0.5m, CallsPerDay = 50, ReservePerCallUsd = 0.05m, SmallModelOnly = true }, options);
    }
}
