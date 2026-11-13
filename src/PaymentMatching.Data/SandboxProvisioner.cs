using Microsoft.EntityFrameworkCore;
using PaymentMatching.AiGateway;
using PaymentMatching.DataGen;
using PaymentMatching.DataGen.Csv;
using PaymentMatching.Matching;

namespace PaymentMatching.Data;

/// <summary>Who signed in: the minimum we keep (SPEC 9b). Provider + subject is the identity.</summary>
public sealed record SignInIdentity(string Provider, string Subject, string Name, string Email);

public sealed record SandboxOptions
{
    /// <summary>Payments in a new sandbox; with the generator's default mix that is ~30 invoices (SPEC 9b).</summary>
    public int StarterPayments { get; init; } = 20;

    /// <summary>New sandboxes per 24 hours; more sign-ups wait (keeps database and AI costs bounded).</summary>
    public int MaxNewPerDay { get; init; } = 5;
}

public enum ProvisionStatus
{
    Existing,
    Created,
    DailyLimitReached,
}

public sealed record ProvisionResult(ProvisionStatus Status, Guid? TenantId);

/// <summary>
/// The week's starter set (ADR 0017): every sandbox created in the same week (Monday to Sunday, UTC) starts
/// from the same synthetic data, so the AI answers for it are computed once by the daily job and reused from
/// the response cache at every sign-in. Each sandbox still gets its own copy of the rows.
/// </summary>
public static class StarterSet
{
    public static DateOnly WeekOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static int SeedOf(DateOnly monday) => (monday.Year * 10000) + (monday.Month * 100) + monday.Day;

    /// <summary>The routes of the daily job: the cache key contains the model, so sign-in must ask the same way.</summary>
    public static GatewayOptions Routes(bool smallModelOnly) =>
        smallModelOnly ? ModelSettings.Default with { Large = null } : ModelSettings.Default;
}

/// <summary>
/// Answers for the AI payments of a new sandbox at sign-in: the response cache of <paramref name="Source"/>
/// only ("live" in production). The model behind it is never called (ADR 0017).
/// </summary>
public sealed record InstantAiOptions(DbContextOptions<OperationsDbContext> Operations, string Source, bool SmallModelOnly);

/// <summary>
/// Gives every signed-in person their own sandbox. On the first sign-in it creates the tenant, the user and
/// the week's starter data, runs the rules layer and takes the AI decisions from the response cache: a
/// sign-in never calls a model. Payments without a cached answer stay undecided for the scheduled job.
/// </summary>
public sealed class SandboxProvisioner(
    DbContextOptions<PaymentsDbContext> options, SandboxOptions settings, TimeProvider time, InstantAiOptions? instantAi = null)
{
    private readonly TenantAdmin _admin = new(options);

    public async Task<ProvisionResult> EnsureAsync(SignInIdentity identity, CancellationToken cancellationToken = default)
    {
        if (await _admin.FindTenantOfAsync(identity.Provider, identity.Subject, cancellationToken) is { } existing)
        {
            return new ProvisionResult(ProvisionStatus.Existing, existing);
        }

        var now = time.GetUtcNow();
        if (await _admin.CountCreatedSinceAsync(now.AddDays(-1), cancellationToken) >= settings.MaxNewPerDay)
        {
            return new ProvisionResult(ProvisionStatus.DailyLimitReached, null);
        }

        var tenantId = Guid.NewGuid();
        await using var db = new PaymentsDbContext(options, new TenantContext(tenantId));
        var week = StarterSet.WeekOf(DateOnly.FromDateTime(now.UtcDateTime));
        var seed = StarterSet.SeedOf(week);
        db.Tenants.Add(new TenantRow { Id = tenantId, CreatedAt = now, LastActiveAt = now, DataSeed = seed });
        db.Users.Add(new UserRow
        {
            Id = Guid.NewGuid(),
            Provider = identity.Provider,
            Subject = identity.Subject,
            Name = Truncate(identity.Name, 200),
            Email = Truncate(identity.Email, 320),
            CreatedAt = now,
        });

        var data = StarterData(seed, week);
        TenantData.Add(db, data);
        var decisions = await DbDecisionStore.LoadAsync(db, cancellationToken);
        var cacheOnly = instantAi is null
            ? null
            : new AiGateway.AiGateway(
                new CachingProvider(instantAi.Operations, instantAi.Source, NoModel.Instance, time),
                StarterSet.Routes(instantAi.SmallModelOnly) with { MaxCalls = 0 },
                PriceTable.Claude);
        await DecideStarterAsync(data, decisions, cacheOnly, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same person signed in twice at once; the other request created the sandbox.
            var raced = await _admin.FindTenantOfAsync(identity.Provider, identity.Subject, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return new ProvisionResult(ProvisionStatus.Existing, raced);
        }

        return new ProvisionResult(ProvisionStatus.Created, tenantId);
    }

    /// <summary>Seed of the demo's starter data: every deployment shows the same demo.</summary>
    public const int DemoSeed = 20261112;

    /// <summary>
    /// Creates the shared read-only demo sandbox (SPEC 9b) when it does not exist: starter data and the
    /// rules layer only, no user (so no sign-in can ever lead into it). The daily job calls this; the id
    /// comes from server configuration. Returns true when it was created now.
    /// </summary>
    public async Task<bool> EnsureDemoAsync(Guid demoId, CancellationToken cancellationToken = default)
    {
        await using var db = new PaymentsDbContext(options, new TenantContext(demoId));
        if (await db.Tenants.AnyAsync(cancellationToken))
        {
            return false;
        }

        var now = time.GetUtcNow();
        db.Tenants.Add(new TenantRow { Id = demoId, CreatedAt = now, LastActiveAt = now, DataSeed = DemoSeed, IsDemo = true });
        var data = StarterData(DemoSeed, DateOnly.FromDateTime(now.UtcDateTime));
        TenantData.Add(db, data);
        var decisions = await DbDecisionStore.LoadAsync(db, cancellationToken);
        await new MatchingPipeline(data, gateway: null, decisions, MatchingOptions.Default with { DeferAiWithoutGateway = true }, time)
            .RunAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Decides starter data the same way everywhere – at sign-in (cache only) and in the job's precomputation
    /// (a model within the budgets) – so the prompts and therefore the cache keys are identical: first the
    /// rules layer over all payments, then the AI layer over the rest. Without a gateway AI payments wait;
    /// over the gateway's call budget they wait too (a sign-in has a budget of zero calls).
    /// </summary>
    public async Task DecideStarterAsync(MatchingDataset data, IDecisionStore decisions, AiGateway.AiGateway? gateway, CancellationToken cancellationToken)
    {
        await new MatchingPipeline(data, gateway: null, decisions, MatchingOptions.Default with { DeferAiWithoutGateway = true }, time)
            .RunAsync(cancellationToken);
        if (gateway is not null)
        {
            await new MatchingPipeline(data, gateway, decisions, MatchingOptions.Default with { DeferAiWhenBudgetExhausted = true }, time)
                .RunAsync(cancellationToken);
        }
    }

    /// <summary>Synthetic starter data, parsed by the same code that reads the generator's CSV files.</summary>
    public MatchingDataset StarterData(int seed, DateOnly today)
    {
        var dataset = DataGenerator.Generate(new GeneratorOptions { Seed = seed, PaymentCount = settings.StarterPayments, ReferenceDate = today });
        var csv = DatasetCsv.ToCsv(dataset);
        return MatchingDataset.Parse(csv[DatasetCsv.CustomersFile], csv[DatasetCsv.InvoicesFile], csv[DatasetCsv.StatementFile]);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>Behind the cache at sign-in. With a budget of zero calls the gateway never reaches it; if it ever did, it fails.</summary>
    private sealed class NoModel : ILlmProvider
    {
        public static readonly NoModel Instance = new();

        public Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A sign-in never calls a model.");
    }
}

/// <summary>Keeps <see cref="TenantRow.LastActiveAt"/> current, at most one write per tenant and day.</summary>
public static class SandboxActivity
{
    public static Task<int> TouchAsync(PaymentsDbContext db, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var dayAgo = now.AddDays(-1);
        return db.Tenants.Where(t => t.LastActiveAt < dayAgo).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastActiveAt, now), cancellationToken);
    }
}
