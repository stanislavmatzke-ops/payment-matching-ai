using Microsoft.EntityFrameworkCore;

namespace PaymentMatching.Data;

/// <summary>
/// The only code allowed to look across tenants (a test checks that no other file in src/ bypasses
/// the tenant filters). It answers "which tenant is this sign-in?", counts new sandboxes for the
/// daily limit and deletes inactive sandboxes. It never returns another tenant's business data.
/// </summary>
public sealed class TenantAdmin(DbContextOptions<PaymentsDbContext> options)
{
    /// <summary>Sandboxes nobody used for this long are deleted (SPEC 9b).</summary>
    public static readonly TimeSpan InactiveAfter = TimeSpan.FromDays(30);

    public async Task<Guid?> FindTenantOfAsync(string provider, string subject, CancellationToken cancellationToken = default)
    {
        await using var db = NoTenant();
        return await db.Users.IgnoreQueryFilters()
            .Where(u => u.Provider == provider && u.Subject == subject)
            .Select(u => (Guid?)u.TenantId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<int> CountCreatedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var db = NoTenant();
        return await db.Tenants.IgnoreQueryFilters().CountAsync(t => t.CreatedAt >= since, cancellationToken);
    }

    /// <summary>Sandboxes used this recently get the daily batch (ADR 0013).</summary>
    public static readonly TimeSpan ActiveWithin = TimeSpan.FromDays(7);

    /// <summary>Deletes sandboxes inactive for <see cref="InactiveAfter"/>, with all their rows; never the demo. Returns how many.</summary>
    public async Task<int> DeleteInactiveAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var cutoff = now - InactiveAfter;
        await using var db = NoTenant();
        return await db.Tenants.IgnoreQueryFilters().Where(t => t.LastActiveAt < cutoff && !t.IsDemo).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Ids of the sandboxes the daily job works on: used within <see cref="ActiveWithin"/>, plus the demo. Ids only.</summary>
    public async Task<IReadOnlyList<Guid>> ActiveTenantIdsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var since = now - ActiveWithin;
        await using var db = NoTenant();
        return await db.Tenants.IgnoreQueryFilters()
            .Where(t => t.LastActiveAt >= since || t.IsDemo)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Counts for the admin overview (ADR 0013): numbers only, no ids, names or business data.</summary>
    public async Task<SandboxSummary> SummaryAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var active = now - ActiveWithin;
        var day = now.AddDays(-1);
        var week = now.AddDays(-7);
        await using var db = NoTenant();
        var people = db.Tenants.IgnoreQueryFilters().Where(t => !t.IsDemo);
        return new SandboxSummary(
            await people.CountAsync(cancellationToken),
            await people.CountAsync(t => t.LastActiveAt >= active, cancellationToken),
            await people.CountAsync(t => t.CreatedAt >= day, cancellationToken),
            await people.CountAsync(t => t.CreatedAt >= week, cancellationToken),
            await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.IsDemo, cancellationToken));
    }

    private PaymentsDbContext NoTenant() => new(options, new TenantContext());
}

/// <summary>Sandboxes of people (the demo not counted) for the admin overview.</summary>
public sealed record SandboxSummary(int Total, int ActiveLast7Days, int CreatedLast24Hours, int CreatedLast7Days, bool DemoExists);
