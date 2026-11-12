using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using PaymentMatching.Data;
using PaymentMatching.Matching;
using PaymentMatching.Testing;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace PaymentMatching.Mcp.Tests;

/// <summary>The MCP server on the app database: it sees one tenant only, chosen at start, never by a tool.</summary>
public class TenantWorkspaceTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<(string Cs, Guid A, Guid B)> TwoSandboxes(PostgresFixture postgres)
    {
        var cs = await postgres.NewDatabaseAsync(Ct);
        var provisioner = new SandboxProvisioner(PaymentsDb.Options(cs), new SandboxOptions(), new FixedTime(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero)));
        var a = (await provisioner.EnsureAsync(new SignInIdentity("dev", "alice", "alice", "alice@dev.local"), Ct)).TenantId!.Value;
        var b = (await provisioner.EnsureAsync(new SignInIdentity("dev", "bob", "bob", "bob@dev.local"), Ct)).TenantId!.Value;

        await using var db = PaymentsDb.ForTenant(cs, b);
        db.Payments.Add(new PaymentRow
        {
            Id = "P9999",
            Seq = 99,
            Amount = 1,
            Currency = "CZK",
            CounterpartyIban = "",
            CounterpartyName = "Bob only",
            VariableSymbol = "",
            Message = "",
            RawLine = "P9999",
        });
        await db.SaveChangesAsync(Ct);
        return (cs, a, b);
    }

    [Fact]
    public async Task Tools_see_only_the_tenant_the_process_was_started_for()
    {
        var (cs, a, _) = await TwoSandboxes(postgres);
        var workspace = await Workspace.LoadAsync(PaymentsDb.ForTenant(cs, a), Ct);
        var queries = new PaymentQueries(workspace);

        await using var db = PaymentsDb.ForTenant(cs, a);
        var own = await db.Payments.OrderBy(p => p.Seq).Select(p => p.Id).ToListAsync(Ct);
        Assert.Equal(own, workspace.Data.Payments.Select(p => p.Id));
        Assert.DoesNotContain(workspace.Data.Payments, p => p.Id == "P9999");
        var unmatched = queries.ListUnmatched(50);
        Assert.All(unmatched.Payments, p => Assert.Contains(p.Id, own));
        Assert.Contains(unmatched.Payments, p => p.Status == PaymentStatus.NotProcessed);

        var error = Assert.Throws<McpException>(() => queries.GetPayment("P9999"));
        Assert.Contains("Unknown payment", error.Message, StringComparison.Ordinal);
        Assert.Throws<McpException>(() => queries.ProposeMatch("P9999", [workspace.Data.Invoices[0].Number], "cross-tenant attempt"));
    }

    [Fact]
    public async Task Proposals_are_saved_under_the_processs_tenant_only()
    {
        var (cs, a, b) = await TwoSandboxes(postgres);
        var workspace = await Workspace.LoadAsync(PaymentsDb.ForTenant(cs, a), Ct);
        var queries = new PaymentQueries(workspace);
        var payment = queries.ListUnmatched(50).Payments.First();
        var invoice = workspace.Data.Invoices[0].Number;

        var result = queries.ProposeMatch(payment.Id, [invoice], "Amount and customer fit.");
        var again = queries.ProposeMatch(payment.Id, [invoice], "Amount and customer fit.");

        Assert.True(result.Created);
        Assert.False(again.Created);
        await using var dbA = PaymentsDb.ForTenant(cs, a);
        await using var dbB = PaymentsDb.ForTenant(cs, b);
        Assert.Equal(result.ProposalId, (await dbA.Proposals.SingleAsync(Ct)).Id);
        Assert.False(await dbB.Proposals.AnyAsync(Ct));

        // A restarted server sees the proposal again.
        var restarted = await Workspace.LoadAsync(PaymentsDb.ForTenant(cs, a), Ct);
        Assert.Single(restarted.Proposals.ForPayment(payment.Id));
    }

    [Fact]
    public async Task Unknown_tenant_does_not_start()
    {
        var (cs, _, _) = await TwoSandboxes(postgres);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Workspace.LoadAsync(PaymentsDb.ForTenant(cs, Guid.NewGuid()), Ct));
    }
}
