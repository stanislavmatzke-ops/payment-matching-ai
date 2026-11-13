using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PaymentMatching.Data;

/// <summary>
/// A stored model answer, reused for an identical request in any sandbox (ADR 0013). The key is the
/// record/replay key of the request (ADR 0007): model, settings, system prompt, schema and the whole
/// user content. A hit therefore means the sandbox sent exactly the same content; the answer reveals
/// nothing it did not already have. <see cref="Source"/> keeps answers of the stub apart from a model's.
/// </summary>
public sealed class CachedAnswerRow
{
    public required string Source { get; set; }

    public required string Key { get; set; }

    public required string Model { get; set; }

    /// <summary>The provider's <c>LlmResponse</c> as JSON.</summary>
    public required string Response { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public int Hits { get; set; }

    public DateTimeOffset? LastHitAt { get; set; }
}

/// <summary>App-wide counters for one day (UTC): model calls and their cost, cache hits, sandboxes.</summary>
public sealed class DailyUsageRow
{
    public DateOnly Day { get; set; }

    public int AiCalls { get; set; }

    public decimal AiCostUsd { get; set; }

    public int CacheHits { get; set; }

    public int SandboxesRefilled { get; set; }

    public int SandboxesDeleted { get; set; }

    /// <summary>First sign-ins turned away because the daily limit of new sandboxes was reached (the waiting list).</summary>
    public int SignInsTurnedAway { get; set; }
}

/// <summary>
/// One key of the web app's ASP.NET Core Data Protection key ring (cookies, antiforgery tokens), as the
/// framework's XML. Kept in the database so a sign-in survives a restart or a scale-to-zero (ADR 0016).
/// </summary>
public sealed class DataProtectionKeyRow
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public required string Xml { get; set; }
}

/// <summary>
/// Operational tables of the hosted app that belong to no tenant: the response cache and the daily
/// usage ledger behind the AI budgets. Kept apart from <see cref="PaymentsDbContext"/>, where every
/// entity is tenant-owned and filtered; nothing here holds a person's data. Its own migrations history.
/// </summary>
public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : DbContext(options)
{
    public DbSet<CachedAnswerRow> CachedAnswers => Set<CachedAnswerRow>();

    public DbSet<DailyUsageRow> DailyUsage => Set<DailyUsageRow>();

    public DbSet<DataProtectionKeyRow> DataProtectionKeys => Set<DataProtectionKeyRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DataProtectionKeyRow>(e =>
        {
            e.ToTable("data_protection_keys");
            e.HasKey(k => k.Id);
            e.Property(k => k.Name).HasMaxLength(128);
        });

        modelBuilder.Entity<CachedAnswerRow>(e =>
        {
            e.ToTable("ai_response_cache");
            e.HasKey(c => new { c.Source, c.Key });
            e.Property(c => c.Source).HasMaxLength(16);
            e.Property(c => c.Key).HasMaxLength(64);
            e.Property(c => c.Model).HasMaxLength(64);
            e.Property(c => c.Response).HasColumnType("jsonb");
        });

        modelBuilder.Entity<DailyUsageRow>(e =>
        {
            e.ToTable("daily_usage");
            e.HasKey(d => d.Day);
            e.Property(d => d.AiCostUsd).HasPrecision(18, 6);
        });
    }
}

public static class OperationsDb
{
    public const string HistoryTable = "__ops_migrations_history";

    public static DbContextOptions<OperationsDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<OperationsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryTable))
            .Options;

    /// <summary>Applies the migrations of both contexts (the app's data and the operational tables).</summary>
    public static async Task MigrateAllAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using (var payments = new PaymentsDbContext(PaymentsDb.Options(connectionString), new TenantContext()))
        {
            await payments.Database.MigrateAsync(cancellationToken);
        }

        await using var operations = new OperationsDbContext(Options(connectionString));
        await operations.Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Used only by <c>dotnet ef migrations --context OperationsDbContext</c>; it never connects.</summary>
internal sealed class OperationsDesignTimeFactory : IDesignTimeDbContextFactory<OperationsDbContext>
{
    public OperationsDbContext CreateDbContext(string[] args) => new(OperationsDb.Options("Host=localhost;Database=payments_design"));
}
