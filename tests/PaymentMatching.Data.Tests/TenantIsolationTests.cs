using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.Testing;

namespace PaymentMatching.Data.Tests;

/// <summary>Cross-tenant leak tests: the data layer itself must keep tenants apart (SPEC 9b).</summary>
public class TenantIsolationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> TenantTables { get; } = new(
        new PaymentsDbContext(PaymentsDb.Options("Host=unused"), new TenantContext()).Model.GetEntityTypes().Select(t => t.ClrType.Name));

    [Theory]
    [MemberData(nameof(TenantTables))]
    public async Task Every_table_returns_only_the_current_tenants_rows(string entity)
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        var method = typeof(TenantIsolationTests).GetMethod(nameof(Owners), BindingFlags.NonPublic | BindingFlags.Static)!;
        var type = typeof(PaymentsDbContext).Assembly.GetTypes().Single(t => t.Name == entity);
        async Task<(List<Guid> Visible, int All)> Read(Guid tenant)
        {
            await using var db = tenants.For(tenant);
            return await (Task<(List<Guid>, int)>)method.MakeGenericMethod(type).Invoke(null, [db])!;
        }

        var (seenByA, all) = await Read(tenants.A);
        var (seenByB, _) = await Read(tenants.B);

        Assert.NotEmpty(seenByA);
        Assert.All(seenByA, owner => Assert.Equal(tenants.A, owner));
        Assert.All(seenByB, owner => Assert.Equal(tenants.B, owner));
        Assert.Equal(all, seenByA.Count + seenByB.Count);
    }

    [Fact]
    public void Every_entity_is_tenant_owned_and_filtered()
    {
        using var db = new PaymentsDbContext(PaymentsDb.Options("Host=unused"), new TenantContext());
        Assert.All(db.Model.GetEntityTypes(), type =>
        {
            Assert.True(type.ClrType == typeof(TenantRow) || typeof(ITenantOwned).IsAssignableFrom(type.ClrType), $"{type.ClrType.Name} has no tenant.");
            Assert.NotEmpty(type.GetDeclaredQueryFilters());
        });
    }

    [Fact]
    public async Task Same_ids_in_two_tenants_never_mix()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        var paymentId = tenants.Data.Payments[0].Id;
        await using var a = tenants.For(tenants.A);

        Assert.Null(await a.Payments.FindAsync([tenants.B, paymentId], Ct));
        Assert.Null(await a.Tenants.FindAsync([tenants.B], Ct));
        var own = await a.Payments.SingleAsync(p => p.Id == paymentId, Ct);
        Assert.Equal(tenants.A, own.TenantId);
        Assert.Single(await a.Users.ToListAsync(Ct));
        Assert.Single(await a.Proposals.Where(p => p.PaymentId == paymentId).ToListAsync(Ct));
    }

    [Fact]
    public async Task Changing_or_deleting_another_tenants_row_is_refused()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        PaymentRow foreign;
        await using (var b = tenants.For(tenants.B))
        {
            foreign = await b.Payments.AsNoTracking().FirstAsync(Ct);
        }

        await using (var a = tenants.For(tenants.A))
        {
            a.Payments.Attach(foreign);
            foreign.Message = "changed by A";
            await Assert.ThrowsAsync<TenantIsolationException>(() => a.SaveChangesAsync(Ct));
        }

        await using (var a = tenants.For(tenants.A))
        {
            a.Payments.Remove(foreign);
            await Assert.ThrowsAsync<TenantIsolationException>(() => a.SaveChangesAsync(Ct));
        }

        await using (var a = tenants.For(tenants.A))
        {
            // Not even through a bulk update: the filter limits it to A's rows.
            await a.Payments.ExecuteUpdateAsync(s => s.SetProperty(p => p.Message, "bulk by A"), Ct);
        }

        await using var check = tenants.For(tenants.B);
        Assert.DoesNotContain(await check.Payments.Select(p => p.Message).ToListAsync(Ct), m => m.Contains("by A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inserting_a_row_for_another_tenant_is_refused()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var a = tenants.For(tenants.A);

        a.Customers.Add(new CustomerRow { TenantId = tenants.B, Id = "C999", Name = "Smuggled s.r.o.", Iban = "CZ00" });

        await Assert.ThrowsAsync<TenantIsolationException>(() => a.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task New_rows_get_the_current_tenant()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using (var a = tenants.For(tenants.A))
        {
            a.Customers.Add(new CustomerRow { Id = "C999", Name = "Nová s.r.o.", Iban = "CZ00" });
            await a.SaveChangesAsync(Ct);
        }

        await using var b = tenants.For(tenants.B);
        Assert.False(await b.Customers.AnyAsync(c => c.Id == "C999", Ct));
    }

    [Fact]
    public async Task Without_a_tenant_nothing_can_be_read_or_written()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var db = tenants.WithoutTenant();

        await Assert.ThrowsAsync<TenantIsolationException>(() => db.Payments.ToListAsync(Ct));
        await Assert.ThrowsAsync<TenantIsolationException>(() => db.Tenants.CountAsync(Ct));
        db.Customers.Add(new CustomerRow { Id = "C999", Name = "x", Iban = "x" });
        await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public void Tenant_of_an_operation_cannot_change()
    {
        var context = new TenantContext(Guid.NewGuid());

        Assert.Throws<TenantIsolationException>(() => context.Set(Guid.NewGuid()));
        Assert.Throws<TenantIsolationException>(() => new TenantContext().TenantId);
    }

    [Fact]
    public void Only_TenantAdmin_bypasses_the_tenant_filters()
    {
        var bypass = new Regex(@"IgnoreQueryFilters|FromSqlRaw|ExecuteSqlRaw|SqlQueryRaw|FromSql\(|ExecuteSql\(|SqlQuery\(", RegexOptions.CultureInvariant);
        var offenders = Directory.EnumerateFiles(RepoPaths.Combine("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "TenantAdmin.cs")
            .Where(f => bypass.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepoPaths.Root, f))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task Migrations_match_the_model()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var db = tenants.WithoutTenant();

        Assert.False(db.Database.HasPendingModelChanges(), "Run: dotnet ef migrations add <Name> --project src/PaymentMatching.Data");
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));

        await using var operations = new OperationsDbContext(OperationsDb.Options(tenants.ConnectionString));
        Assert.False(operations.Database.HasPendingModelChanges(), "Run: dotnet ef migrations add <Name> --project src/PaymentMatching.Data --context OperationsDbContext --output-dir OperationsMigrations");
        Assert.Empty(await operations.Database.GetPendingMigrationsAsync(Ct));
    }

    private static async Task<(List<Guid> Visible, int All)> Owners<T>(PaymentsDbContext db)
        where T : class
    {
        var visible = await db.Set<T>().ToListAsync(Ct);
        var owners = visible.Select(row => row switch
        {
            ITenantOwned owned => owned.TenantId,
            TenantRow tenant => tenant.Id,
            _ => throw new InvalidOperationException(typeof(T).Name),
        }).ToList();

        // The test may look past the filter; production code may not (see Only_TenantAdmin_bypasses_the_tenant_filters).
        var all = await db.Set<T>().IgnoreQueryFilters().CountAsync(Ct);
        return (owners, all);
    }
}
