using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace PaymentMatching.Data;

/// <summary>
/// The app's database, seen through one tenant. Isolation is enforced here, not in callers:
/// <list type="bullet">
/// <item>every tenant-owned entity (and the tenant row itself) has a query filter on the current tenant,</item>
/// <item>saving stamps new rows with the current tenant and refuses to add, change or delete another tenant's row,</item>
/// <item>without a tenant every query and every save throws (fail closed).</item>
/// </list>
/// Only <see cref="TenantAdmin"/> may look across tenants (sign-in lookup, sandbox clean-up).
/// </summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<TenantRow> Tenants => Set<TenantRow>();

    public DbSet<UserRow> Users => Set<UserRow>();

    public DbSet<CustomerRow> Customers => Set<CustomerRow>();

    public DbSet<InvoiceRow> Invoices => Set<InvoiceRow>();

    public DbSet<PaymentRow> Payments => Set<PaymentRow>();

    public DbSet<DecisionRow> Decisions => Set<DecisionRow>();

    public DbSet<InvoiceMatchRow> InvoiceMatches => Set<InvoiceMatchRow>();

    public DbSet<ProposalRow> Proposals => Set<ProposalRow>();

    /// <summary>Read by the query filters at query time (EF Core parameterizes it per context instance).</summary>
    internal Guid CurrentTenantId => tenant.TenantId;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantRow>(e =>
        {
            e.ToTable("tenants");
            e.HasQueryFilter(t => t.Id == CurrentTenantId);
            e.HasIndex(t => t.CreatedAt);
            e.HasIndex(t => t.LastActiveAt);
        });

        modelBuilder.Entity<UserRow>(e =>
        {
            e.ToTable("users");
            e.HasIndex(u => new { u.Provider, u.Subject }).IsUnique();
            e.HasIndex(u => u.TenantId).IsUnique();
            e.Property(u => u.Provider).HasMaxLength(32);
            e.Property(u => u.Subject).HasMaxLength(255);
            e.Property(u => u.Name).HasMaxLength(200);
            e.Property(u => u.Email).HasMaxLength(320);
        });

        modelBuilder.Entity<CustomerRow>(e =>
        {
            e.ToTable("customers");
            e.HasKey(c => new { c.TenantId, c.Id });
        });

        modelBuilder.Entity<InvoiceRow>(e =>
        {
            e.ToTable("invoices");
            e.HasKey(i => new { i.TenantId, i.Number });
            e.Property(i => i.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<PaymentRow>(e =>
        {
            e.ToTable("payments");
            e.HasKey(p => new { p.TenantId, p.Id });
            e.HasIndex(p => new { p.TenantId, p.Seq });
            e.Property(p => p.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<DecisionRow>(e =>
        {
            e.ToTable("decisions");
            e.HasKey(d => new { d.TenantId, d.IdempotencyKey });
            e.HasIndex(d => new { d.TenantId, d.PaymentId });
            e.Property(d => d.Record).HasColumnType("jsonb");
        });

        modelBuilder.Entity<InvoiceMatchRow>(e =>
        {
            e.ToTable("invoice_matches");
            e.HasKey(m => new { m.TenantId, m.InvoiceNumber });
        });

        modelBuilder.Entity<ProposalRow>(e =>
        {
            e.ToTable("proposals");
            e.HasKey(p => new { p.TenantId, p.Id });
            e.HasIndex(p => new { p.TenantId, p.PaymentId });
            e.Property(p => p.Record).HasColumnType("jsonb");
        });

        // Every tenant-owned type: a filter on the current tenant and a cascade from the tenant row.
        foreach (var type in modelBuilder.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            GetType().GetMethod(nameof(ConfigureTenantOwned), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(type.ClrType)
                .Invoke(this, [modelBuilder]);
        }
    }

    private void ConfigureTenantOwned<T>(ModelBuilder modelBuilder)
        where T : class, ITenantOwned
    {
        modelBuilder.Entity<T>(e =>
        {
            e.HasQueryFilter(row => row.TenantId == CurrentTenantId);
            e.HasOne<TenantRow>().WithMany().HasForeignKey(row => row.TenantId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private void EnforceTenant()
    {
        var entries = ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var current = CurrentTenantId;
        foreach (var entry in entries)
        {
            if (entry.Entity is ITenantOwned owned && entry.State == EntityState.Added && owned.TenantId == Guid.Empty)
            {
                owned.TenantId = current;
            }

            var owner = OwnerOf(entry);
            if (owner != current)
            {
                throw new TenantIsolationException(
                    $"{entry.Metadata.ClrType.Name} belongs to another tenant; {entry.State} refused.");
            }
        }
    }

    /// <summary>The tenant a row belongs to; for a changed row, the tenant it belonged to when it was read.</summary>
    private static Guid OwnerOf(EntityEntry entry)
    {
        var property = entry.Entity switch
        {
            ITenantOwned => entry.Property(nameof(ITenantOwned.TenantId)),
            TenantRow => entry.Property(nameof(TenantRow.Id)),
            _ => throw new TenantIsolationException($"{entry.Metadata.ClrType.Name} is not tenant-owned and cannot be saved here."),
        };
        var now = (Guid)property.CurrentValue!;
        var then = entry.State == EntityState.Added ? now : (Guid)property.OriginalValue!;
        return now == then ? now : Guid.Empty;
    }
}
