using Microsoft.EntityFrameworkCore;

namespace PaymentMatching.Data;

/// <summary>
/// The only code allowed to look across tenants (a test checks that no other file in src/ bypasses
/// the tenant filters). It answers "which tenant is this sign-in?", counts new sandboxes for the
/// daily limit, deletes inactive sandboxes and lists the accounts for the operator. It never returns
/// another tenant's business data (payments, invoices, decisions, statement text).
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

    /// <summary>
    /// The accounts for the operator's overview (ADR 0014 amendment): who signed in, when, and how many payments
    /// they decided. Identity and dates only – never a payment, invoice, text or tenant id.
    /// </summary>
    public async Task<IReadOnlyList<AccountSummary>> AccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = NoTenant();
        var reviews = await db.Reviews.IgnoreQueryFilters()
            .GroupBy(r => new { r.TenantId, r.Action })
            .Select(g => new { g.Key.TenantId, g.Key.Action, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var accounts = await db.Users.IgnoreQueryFilters()
            .Join(db.Tenants.IgnoreQueryFilters(), u => u.TenantId, t => t.Id, (u, t) => new { u.TenantId, u.Name, u.Email, u.Provider, t.CreatedAt, t.LastActiveAt })
            .OrderByDescending(a => a.LastActiveAt)
            .ToListAsync(cancellationToken);
        int Count(Guid tenant, string action) => reviews.Where(r => r.TenantId == tenant && r.Action == action).Sum(r => r.Count);
        return accounts
            .Select(a => new AccountSummary(
                a.Name,
                a.Email,
                a.Provider,
                a.CreatedAt,
                a.LastActiveAt,
                Count(a.TenantId, ReviewAction.Confirmed),
                Count(a.TenantId, ReviewAction.Rejected),
                Count(a.TenantId, ReviewAction.MatchedManually)))
            .ToList();
    }

    private PaymentsDbContext NoTenant() => new(options, new TenantContext());
}

/// <summary>One signed-in account for the operator: identity, sandbox dates and the person's decisions (counts).</summary>
public sealed record AccountSummary(
    string Name, string Email, string Provider, DateTimeOffset SandboxCreatedAt, DateTimeOffset LastActiveAt, int Confirmed, int Rejected, int MatchedManually)
{
    public bool ActiveWithin(DateTimeOffset now) => LastActiveAt >= now - TenantAdmin.ActiveWithin;

    /// <summary>Whole days until the sandbox is deleted for inactivity (0 = with the next job run).</summary>
    public int DaysUntilDeletion(DateTimeOffset now) => Math.Max(0, (int)Math.Ceiling((LastActiveAt + TenantAdmin.InactiveAfter - now).TotalDays));
}

/// <summary>Sandboxes of people (the demo not counted) for the admin overview.</summary>
public sealed record SandboxSummary(int Total, int ActiveLast7Days, int CreatedLast24Hours, int CreatedLast7Days, bool DemoExists);
