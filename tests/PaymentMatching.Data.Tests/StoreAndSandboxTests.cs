using Microsoft.EntityFrameworkCore;
using PaymentMatching.AiGateway;
using PaymentMatching.Matching;
using PaymentMatching.Testing;

namespace PaymentMatching.Data.Tests;

public class DecisionStoreTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MatchingDataset Sample { get; } = MatchingDataset.Load(RepoPaths.Combine("data", "sample"));

    private static async Task<RunReport> RunStub(MatchingDataset data, IDecisionStore store) =>
        await new MatchingPipeline(data, new AiGateway.AiGateway(new NaiveModelStub(), ModelSettings.Default, PriceTable.Claude), store, MatchingOptions.Default)
            .RunAsync(Ct);

    private static async Task<(string Cs, Guid Tenant)> NewTenantWithSample(PostgresFixture postgres)
    {
        var cs = await postgres.NewDatabaseAsync(Ct);
        var tenant = Guid.NewGuid();
        await using var db = PaymentsDb.ForTenant(cs, tenant);
        db.Tenants.Add(new TenantRow { Id = tenant, CreatedAt = TwoTenants.Now, LastActiveAt = TwoTenants.Now });
        TenantData.Add(db, Sample);
        await db.SaveChangesAsync(Ct);
        return (cs, tenant);
    }

    [Fact]
    public async Task Pipeline_over_the_database_decides_like_the_jsonl_log_and_never_twice()
    {
        var (cs, tenant) = await NewTenantWithSample(postgres);
        var expected = await RunStub(Sample, new DecisionLog());

        await using (var db = PaymentsDb.ForTenant(cs, tenant))
        {
            var data = await TenantData.LoadDatasetAsync(db, Ct);
            Assert.Equal(Sample.Payments, data.Payments);
            var first = await RunStub(data, await DbDecisionStore.LoadAsync(db, Ct));
            await db.SaveChangesAsync(Ct);
            Assert.Equal(expected.Records.Select(r => r.Decision), first.Records.Select(r => r.Decision), DecisionComparer.Instance);
        }

        await using (var db = PaymentsDb.ForTenant(cs, tenant))
        {
            var store = await DbDecisionStore.LoadAsync(db, Ct);
            var second = await RunStub(await TenantData.LoadDatasetAsync(db, Ct), store);
            Assert.Equal(Sample.Payments.Count, second.Reused);
            Assert.Equal(0, await db.SaveChangesAsync(Ct));
            Assert.Equal(Sample.Payments.Count, await db.Decisions.CountAsync(Ct));
            Assert.Equal(store.MatchedInvoices.Count, await db.InvoiceMatches.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task Database_refuses_a_second_decision_for_the_same_payment()
    {
        var (cs, tenant) = await NewTenantWithSample(postgres);
        var payment = Sample.Payments[0];
        var record = Record(payment, Outcome.HumanReview);

        // Two concurrent runs: both loaded before either saved, so only the database can stop the second.
        await using var first = PaymentsDb.ForTenant(cs, tenant);
        await using var second = PaymentsDb.ForTenant(cs, tenant);
        var storeA = await DbDecisionStore.LoadAsync(first, Ct);
        var storeB = await DbDecisionStore.LoadAsync(second, Ct);
        storeA.Append(record);
        storeB.Append(record);

        await first.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Database_refuses_matching_one_invoice_to_two_payments()
    {
        var (cs, tenant) = await NewTenantWithSample(postgres);
        var invoice = Sample.Invoices[0].Number;

        await using var first = PaymentsDb.ForTenant(cs, tenant);
        await using var second = PaymentsDb.ForTenant(cs, tenant);
        (await DbDecisionStore.LoadAsync(first, Ct)).Append(Record(Sample.Payments[0], Outcome.AutoMatched, invoice));
        (await DbDecisionStore.LoadAsync(second, Ct)).Append(Record(Sample.Payments[1], Outcome.AutoMatched, invoice));

        await first.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task The_same_statement_line_can_be_decided_once_in_each_tenant()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var a = tenants.For(tenants.A);
        await using var b = tenants.For(tenants.B);

        var keysA = await a.Decisions.Select(d => d.IdempotencyKey).ToListAsync(Ct);
        var keysB = await b.Decisions.Select(d => d.IdempotencyKey).ToListAsync(Ct);

        Assert.NotEmpty(keysA);
        Assert.Equal(keysA.Order(), keysB.Order());
    }

    [Fact]
    public async Task Proposal_store_is_idempotent_across_processes()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var one = tenants.For(tenants.A);
        await using var two = tenants.For(tenants.A);
        var storeOne = await DbProposalStore.LoadAsync(one, Ct);
        var storeTwo = await DbProposalStore.LoadAsync(two, Ct);
        var payment = tenants.Data.Payments[1];
        var proposal = new Proposal(Proposal.IdFor(payment.Id, ["2026-0001"]), payment.Id, ["2026-0001"], "evidence", Proposal.ByMcpAgent, TwoTenants.Now, []);

        Assert.True(storeOne.Add(proposal).Created);
        Assert.False(storeTwo.Add(proposal with { Reason = "later" }).Created);
        Assert.Equal("evidence", storeTwo.ForPayment(payment.Id).Single().Reason);

        await using var b = tenants.For(tenants.B);
        Assert.Empty((await DbProposalStore.LoadAsync(b, Ct)).ForPayment(payment.Id));
    }

    private static AuditRecord Record(BankPayment payment, Outcome outcome, params string[] invoices) =>
        new(DecisionLog.KeyFor(payment), payment.Id, TwoTenants.Now, AuditRecord.RuleLayer, "TEST", null,
            new Decision(payment.Id, outcome, invoices, "test", "test", []), [], [], []);

    private sealed class DecisionComparer : IEqualityComparer<Decision>
    {
        public static readonly DecisionComparer Instance = new();

        public bool Equals(Decision? x, Decision? y) =>
            x is not null && y is not null && x.PaymentId == y.PaymentId && x.Outcome == y.Outcome && x.DecidedBy == y.DecidedBy
            && x.InvoiceNumbers.SequenceEqual(y.InvoiceNumbers) && x.Flags.SequenceEqual(y.Flags) && x.Confidence == y.Confidence;

        public int GetHashCode(Decision obj) => obj.PaymentId.GetHashCode(StringComparison.Ordinal);
    }
}

public class SandboxTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SignInIdentity Person(string who) => new("dev", who, who, $"{who}@example.test");

    [Fact]
    public async Task First_sign_in_creates_a_sandbox_with_starter_data_and_rule_decisions_only()
    {
        var cs = await postgres.NewDatabaseAsync(Ct);
        var provisioner = new SandboxProvisioner(PaymentsDb.Options(cs), new SandboxOptions(), new FixedTime(TwoTenants.Now));

        var first = await provisioner.EnsureAsync(Person("alice"), Ct);
        var again = await provisioner.EnsureAsync(Person("alice"), Ct);

        Assert.Equal(ProvisionStatus.Created, first.Status);
        Assert.Equal(new ProvisionResult(ProvisionStatus.Existing, first.TenantId), again);

        await using var db = PaymentsDb.ForTenant(cs, first.TenantId!.Value);
        Assert.Equal(20, await db.Payments.CountAsync(Ct));
        Assert.InRange(await db.Invoices.CountAsync(Ct), 25, 40);
        var user = await db.Users.SingleAsync(Ct);
        Assert.Equal(("alice", "alice@example.test"), (user.Name, user.Email));

        var decisions = (await db.Decisions.Select(d => d.Record).ToListAsync(Ct)).Select(DecisionLog.Deserialize).ToList();
        Assert.InRange(decisions.Count, 1, 19);
        Assert.All(decisions, d => Assert.Equal(AuditRecord.RuleLayer, d.Layer));
        Assert.All(decisions, d => Assert.Empty(d.Calls));
    }

    [Fact]
    public void Starter_data_covers_the_spec_case_types()
    {
        var provisioner = new SandboxProvisioner(PaymentsDb.Options("Host=unused"), new SandboxOptions(), TimeProvider.System);
        var dataset = DataGen.DataGenerator.Generate(new DataGen.GeneratorOptions { Seed = 1, PaymentCount = 20 });

        Assert.True(dataset.Expected.Select(e => e.CaseType).Distinct().Count() >= 6);
        Assert.Equal(20, provisioner.StarterData(1, new DateOnly(2026, 11, 12)).Payments.Count);
    }

    [Fact]
    public async Task Simultaneous_first_sign_ins_create_one_sandbox()
    {
        var cs = await postgres.NewDatabaseAsync(Ct);
        var provisioner = new SandboxProvisioner(PaymentsDb.Options(cs), new SandboxOptions(), new FixedTime(TwoTenants.Now));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => provisioner.EnsureAsync(Person("alice"), Ct)));

        Assert.Single(results.Select(r => r.TenantId).Distinct());
        Assert.Single(results, r => r.Status == ProvisionStatus.Created);
        Assert.Equal(1, await new TenantAdmin(PaymentsDb.Options(cs)).CountCreatedSinceAsync(DateTimeOffset.MinValue, Ct));
    }

    [Fact]
    public async Task New_sandboxes_stop_at_the_daily_limit_but_existing_users_still_get_in()
    {
        var cs = await postgres.NewDatabaseAsync(Ct);
        var time = new FixedTime(TwoTenants.Now);
        var provisioner = new SandboxProvisioner(PaymentsDb.Options(cs), new SandboxOptions { MaxNewPerDay = 1 }, time);

        var alice = await provisioner.EnsureAsync(Person("alice"), Ct);
        Assert.Equal(ProvisionStatus.DailyLimitReached, (await provisioner.EnsureAsync(Person("bob"), Ct)).Status);
        Assert.Equal(ProvisionStatus.Existing, (await provisioner.EnsureAsync(Person("alice"), Ct)).Status);

        time.Now = TwoTenants.Now.AddDays(1).AddMinutes(1);
        Assert.Equal(ProvisionStatus.Created, (await provisioner.EnsureAsync(Person("bob"), Ct)).Status);
        Assert.NotNull(alice.TenantId);
    }

    [Fact]
    public async Task Inactive_sandboxes_are_deleted_with_all_their_rows()
    {
        var cs = await postgres.NewDatabaseAsync(Ct);
        var time = new FixedTime(TwoTenants.Now);
        var provisioner = new SandboxProvisioner(PaymentsDb.Options(cs), new SandboxOptions(), time);
        var old = (await provisioner.EnsureAsync(Person("old"), Ct)).TenantId!.Value;
        time.Now = TwoTenants.Now.AddDays(20);
        var active = (await provisioner.EnsureAsync(Person("active"), Ct)).TenantId!.Value;

        var deleted = await new TenantAdmin(PaymentsDb.Options(cs)).DeleteInactiveAsync(TwoTenants.Now.AddDays(31), Ct);

        Assert.Equal(1, deleted);
        Assert.Null(await new TenantAdmin(PaymentsDb.Options(cs)).FindTenantOfAsync("dev", "old", Ct));
        await using var gone = PaymentsDb.ForTenant(cs, old);
        Assert.Equal(0, await gone.Payments.CountAsync(Ct) + await gone.Decisions.CountAsync(Ct) + await gone.Users.CountAsync(Ct));
        await using var kept = PaymentsDb.ForTenant(cs, active);
        Assert.Equal(20, await kept.Payments.CountAsync(Ct));
    }

    [Fact]
    public async Task Activity_touches_only_the_current_tenant_and_at_most_daily()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var a = tenants.For(tenants.A);

        Assert.Equal(0, await SandboxActivity.TouchAsync(a, TwoTenants.Now.AddHours(5), Ct));
        Assert.Equal(1, await SandboxActivity.TouchAsync(a, TwoTenants.Now.AddDays(2), Ct));

        await using var b = tenants.For(tenants.B);
        Assert.Equal(TwoTenants.Now, (await b.Tenants.SingleAsync(Ct)).LastActiveAt);
    }
}
