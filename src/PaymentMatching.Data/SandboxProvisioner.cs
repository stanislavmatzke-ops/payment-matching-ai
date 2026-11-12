using Microsoft.EntityFrameworkCore;
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
    public int MaxNewPerDay { get; init; } = 50;
}

public enum ProvisionStatus
{
    Existing,
    Created,
    DailyLimitReached,
}

public sealed record ProvisionResult(ProvisionStatus Status, Guid? TenantId);

/// <summary>
/// Gives every signed-in person their own sandbox. On the first sign-in it creates the tenant, the
/// user and synthetic starter data, and runs only the rules layer: a sign-in never calls a model.
/// Payments that need AI stay undecided for the scheduled job (phase 9).
/// </summary>
public sealed class SandboxProvisioner(DbContextOptions<PaymentsDbContext> options, SandboxOptions settings, TimeProvider time)
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
        var seed = BitConverter.ToInt32(tenantId.ToByteArray()) & int.MaxValue;
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

        var data = StarterData(seed, DateOnly.FromDateTime(now.UtcDateTime));
        TenantData.Add(db, data);
        var decisions = await DbDecisionStore.LoadAsync(db, cancellationToken);
        await new MatchingPipeline(data, gateway: null, decisions, MatchingOptions.Default with { DeferAiWithoutGateway = true }, time)
            .RunAsync(cancellationToken);

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

    /// <summary>Synthetic starter data, parsed by the same code that reads the generator's CSV files.</summary>
    public MatchingDataset StarterData(int seed, DateOnly today)
    {
        var dataset = DataGenerator.Generate(new GeneratorOptions { Seed = seed, PaymentCount = settings.StarterPayments, ReferenceDate = today });
        var csv = DatasetCsv.ToCsv(dataset);
        return MatchingDataset.Parse(csv[DatasetCsv.CustomersFile], csv[DatasetCsv.InvoicesFile], csv[DatasetCsv.StatementFile]);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
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
