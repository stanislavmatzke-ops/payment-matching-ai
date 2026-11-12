using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.AiGateway;
using PaymentMatching.Data;
using PaymentMatching.Matching;
using PaymentMatching.Testing;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace PaymentMatching.Job.Tests;

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// A model for tests: the stub's answers (or a fixed answer), with token usage so calls cost something,
/// and a log of every request that reached it (a cached answer never does).
/// </summary>
internal sealed class CountingModel(Func<LlmRequest, string>? answer = null) : ILlmProvider
{
    private readonly NaiveModelStub _stub = new();

    public List<LlmRequest> Requests { get; } = [];

    public async Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var json = answer?.Invoke(request) ?? (await _stub.CompleteJsonAsync(request, cancellationToken)).Json;
        return new LlmResponse(request.Model, json, "end_turn", new TokenUsage(1500, 200), TimeSpan.FromMilliseconds(5));
    }

    /// <summary>An answer that names an invoice which is not among the candidates (validation must stop it).</summary>
    public static string WrongInvoice(string invoiceNumber) => JsonSerializer.Serialize(new
    {
        reasoning = "test",
        decision = AiMatchAnswer.Match,
        invoice_numbers = new[] { invoiceNumber },
        confidence = 0.99,
        flags = Array.Empty<string>(),
    });
}

/// <summary>A fresh database per test, sandboxes made to measure, and the job wired as in production.</summary>
internal sealed class JobTestBed
{
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 6, 0, 0, TimeSpan.Zero);

    private JobTestBed(string connectionString) => ConnectionString = connectionString;

    public string ConnectionString { get; }

    public FixedTime Time { get; } = new(Now);

    public DateOnly Today => DateOnly.FromDateTime(Time.Now.UtcDateTime);

    public DbContextOptions<PaymentsDbContext> Payments => PaymentsDb.Options(ConnectionString);

    public DbContextOptions<OperationsDbContext> Operations => OperationsDb.Options(ConnectionString);

    public UsageLedger Ledger => new(Operations);

    public static async Task<JobTestBed> CreateAsync(PostgresFixture postgres) =>
        new(await postgres.NewDatabaseAsync(TestContext.Current.CancellationToken));

    /// <summary>The first day from <see cref="Now"/> whose batch has two cases the model is asked about (not an injection without candidates).</summary>
    public void MoveToDayWithTwoModelCases()
    {
        DataGen.Model.CaseType[] asked = [DataGen.Model.CaseType.UnparsedReference, DataGen.Model.CaseType.VsTypo, DataGen.Model.CaseType.MultiInvoice];
        while (DailyBatch.For(Today).Generated.Expected.Count(e => asked.Contains(e.CaseType)) < 2)
        {
            Time.Now = Time.Now.AddDays(1);
        }
    }

    public DailyJob Job(ILlmProvider model, AiBudgetOptions? budget = null, Guid? demo = null, string source = "live") =>
        new(Payments, Operations, model, new JobSettings(source, demo, budget ?? AiBudgetOptions.Default), Time);

    public PaymentsDbContext For(Guid tenant) => PaymentsDb.ForTenant(ConnectionString, tenant);

    /// <summary>
    /// A person's sandbox. With a seed it gets starter data and the rules layer (AI payments wait, as after
    /// a sign-in); without one it is empty, so only the daily batch is in it.
    /// </summary>
    public async Task<Guid> SandboxAsync(int? seed = null, TimeSpan? lastActiveAgo = null)
    {
        var tenant = Guid.NewGuid();
        await using var db = For(tenant);
        db.Tenants.Add(new TenantRow { Id = tenant, CreatedAt = Time.Now.AddDays(-10), LastActiveAt = Time.Now - (lastActiveAgo ?? TimeSpan.FromHours(1)), DataSeed = seed ?? 0 });
        db.Users.Add(new UserRow { Id = Guid.NewGuid(), Provider = "dev", Subject = tenant.ToString("N"), Name = "tester", Email = "tester@example.test", CreatedAt = Time.Now });
        if (seed is { } s)
        {
            var data = new SandboxProvisioner(Payments, new SandboxOptions(), Time).StarterData(s, Today);
            TenantData.Add(db, data);
            var store = await DbDecisionStore.LoadAsync(db, TestContext.Current.CancellationToken);
            await new MatchingPipeline(data, null, store, MatchingOptions.Default with { DeferAiWithoutGateway = true }, Time)
                .RunAsync(TestContext.Current.CancellationToken);
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return tenant;
    }

    /// <summary>Payments of a sandbox with their state (matched, needs_review, waiting_for_ai, ...).</summary>
    public async Task<Dictionary<string, string>> StatesAsync(Guid tenant)
    {
        await using var db = For(tenant);
        var state = await SandboxState.LoadAsync(db, null, TestContext.Current.CancellationToken);
        return state.Data.Payments.ToDictionary(p => p.Id, p => state.StateOf(p.Id));
    }

    public async Task<List<AuditRecord>> DecisionsAsync(Guid tenant)
    {
        await using var db = For(tenant);
        var store = await DbDecisionStore.LoadAsync(db, TestContext.Current.CancellationToken);
        return store.Records.ToList();
    }
}
