using Microsoft.EntityFrameworkCore;
using PaymentMatching.Matching;
using PaymentMatching.Testing;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace PaymentMatching.Data.Tests;

/// <summary>A time that stands still (tests decide when "now" is).</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Two tenants in a fresh database with <b>identical ids</b> (same customers, invoices, payments,
/// a decision, an invoice match and a proposal). Only the tenant filter can tell their rows apart.
/// </summary>
internal sealed class TwoTenants
{
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    private TwoTenants(string connectionString, Guid a, Guid b, MatchingDataset data)
    {
        ConnectionString = connectionString;
        A = a;
        B = b;
        Data = data;
    }

    public string ConnectionString { get; }

    public Guid A { get; }

    public Guid B { get; }

    public MatchingDataset Data { get; }

    public DbContextOptions<PaymentsDbContext> Options => PaymentsDb.Options(ConnectionString);

    public static async Task<TwoTenants> CreateAsync(PostgresFixture postgres)
    {
        var connectionString = await postgres.NewDatabaseAsync(TestContext.Current.CancellationToken);
        var provisioner = new SandboxProvisioner(PaymentsDb.Options(connectionString), new SandboxOptions(), new FixedTime(Now));
        var data = provisioner.StarterData(seed: 7, DateOnly.FromDateTime(Now.UtcDateTime));
        var tenants = new TwoTenants(connectionString, Guid.NewGuid(), Guid.NewGuid(), data);
        await tenants.FillAsync(tenants.A, "alice");
        await tenants.FillAsync(tenants.B, "bob");
        return tenants;
    }

    public PaymentsDbContext For(Guid tenant) => PaymentsDb.ForTenant(ConnectionString, tenant);

    public PaymentsDbContext WithoutTenant() => new(Options, new TenantContext());

    private async Task FillAsync(Guid tenant, string who)
    {
        await using var db = For(tenant);
        db.Tenants.Add(new TenantRow { Id = tenant, CreatedAt = Now, LastActiveAt = Now, DataSeed = 7 });
        db.Users.Add(new UserRow { Id = Guid.NewGuid(), Provider = "dev", Subject = who, Name = who, Email = $"{who}@example.test", CreatedAt = Now });
        TenantData.Add(db, Data);
        var store = await DbDecisionStore.LoadAsync(db, TestContext.Current.CancellationToken);
        await new MatchingPipeline(Data, null, store, MatchingOptions.Default with { DeferAiWithoutGateway = true }, new FixedTime(Now))
            .RunAsync(TestContext.Current.CancellationToken);
        var payment = Data.Payments[0];
        var proposal = new Proposal(Proposal.IdFor(payment.Id, [Data.Invoices[0].Number]), payment.Id, [Data.Invoices[0].Number], "test", Proposal.ByMcpAgent, Now, []);
        db.Proposals.Add(new ProposalRow { Id = proposal.Id, PaymentId = payment.Id, At = Now, Record = Proposal.Serialize(proposal) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
