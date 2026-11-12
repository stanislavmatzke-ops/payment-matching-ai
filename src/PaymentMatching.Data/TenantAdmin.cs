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

    /// <summary>Deletes sandboxes inactive for <see cref="InactiveAfter"/>, with all their rows. Returns how many.</summary>
    public async Task<int> DeleteInactiveAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var cutoff = now - InactiveAfter;
        await using var db = NoTenant();
        return await db.Tenants.IgnoreQueryFilters().Where(t => t.LastActiveAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private PaymentsDbContext NoTenant() => new(options, new TenantContext());
}
