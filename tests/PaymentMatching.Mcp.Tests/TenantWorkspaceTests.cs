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
    public async Task Decisions_a_person_made_in_the_app_close_the_payment_and_its_invoices_for_agents()
    {
        var (cs, a, _) = await TwoSandboxes(postgres);
        string matchedPayment, rejectedPayment, invoice;
        await using (var db = PaymentsDb.ForTenant(cs, a))
        {
            // Payments the pipeline did not match (the random starter data may have fewer).
            var store = await DbDecisionStore.LoadAsync(db, Ct);
            foreach (var (id, seq) in new[] { ("P0901", 900), ("P0902", 901), ("P0903", 902) })
            {
                var bank = new BankPayment(id, new DateOnly(2026, 11, 10), 100, "CZK", "", "Unknown payer", "", "no reference", $"{id}|no reference");
                db.Payments.Add(new PaymentRow { Id = id, Seq = seq, BookingDate = bank.BookingDate, Amount = 100, Currency = "CZK", CounterpartyIban = "", CounterpartyName = bank.CounterpartyName, VariableSymbol = "", Message = bank.Message, RawLine = bank.RawLine });
                store.Append(new AuditRecord(DecisionLog.KeyFor(bank), id, DateTimeOffset.UtcNow, AuditRecord.PolicyLayer, null, null, new Decision(id, Outcome.NoMatch, [], DecidedBy.Policy, "No related invoice.", []), [], [], []));
            }

            await db.SaveChangesAsync(Ct);
            var state = await SandboxState.LoadAsync(db, null, Ct);
            (matchedPayment, rejectedPayment) = ("P0901", "P0902");
            Assert.True(state.IsActionable(matchedPayment) && state.IsActionable(rejectedPayment));
            invoice = state.OpenInvoices()[0].Number;
            var actions = new ReviewActions(db, TimeProvider.System);
            var preview = await actions.PreviewAsync(matchedPayment, [invoice], Ct);
            Assert.Equal(ReviewOutcome.Done, (await actions.MatchManuallyAsync(matchedPayment, [invoice], preview.Failures, Ct)).Outcome);
            Assert.Equal(ReviewOutcome.Done, (await actions.RejectAsync(rejectedPayment, Ct)).Outcome);
        }

        var workspace = await Workspace.LoadAsync(PaymentsDb.ForTenant(cs, a), Ct);
        var queries = new PaymentQueries(workspace);

        Assert.Equal(matchedPayment, workspace.MatchedInvoices[invoice]);
        foreach (var payment in new[] { matchedPayment, rejectedPayment })
        {
            var error = Assert.Throws<McpException>(() => queries.ProposeMatch(payment, [workspace.Data.Invoices[^1].Number], "Agent tries anyway."));
            Assert.Contains("person has already decided", error.Message, StringComparison.Ordinal);
        }

        var other = "P0903";
        var taken = Assert.Throws<McpException>(() => queries.ProposeMatch(other, [invoice], "Agent proposes a taken invoice."));
        Assert.Contains($"{invoice} → {matchedPayment}", taken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_tenant_does_not_start()
    {
        var (cs, _, _) = await TwoSandboxes(postgres);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Workspace.LoadAsync(PaymentsDb.ForTenant(cs, Guid.NewGuid()), Ct));
    }
}
