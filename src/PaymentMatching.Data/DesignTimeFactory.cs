using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PaymentMatching.Data;

/// <summary>Used only by <c>dotnet ef migrations</c>; it never connects (the connection string is a placeholder).</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) =>
        new(PaymentsDb.Options("Host=localhost;Database=payments_design"), new TenantContext());
}

public static class PaymentsDb
{
    /// <summary>Configuration key of the connection string (user-secrets, environment or appsettings).</summary>
    public const string ConnectionStringName = "Payments";

    public static DbContextOptions<PaymentsDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(connectionString).Options;

    public static PaymentsDbContext ForTenant(string connectionString, Guid tenantId) =>
        new(Options(connectionString), new TenantContext(tenantId));
}
