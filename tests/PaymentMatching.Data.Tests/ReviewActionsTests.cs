using Microsoft.EntityFrameworkCore;
using PaymentMatching.Matching;
using PaymentMatching.Testing;

namespace PaymentMatching.Data.Tests;

/// <summary>A person's actions in the app: confirm, reject, match manually (SPEC 9b), and their tenant isolation.</summary>
public class ReviewActionsTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReviewActions Actions(PaymentsDbContext db) => new(db, new FixedTime(TwoTenants.Now));

    private static async Task<SandboxState> StateAsync(PaymentsDbContext db) => await SandboxState.LoadAsync(db, null, Ct);

    /// <summary>A payment the pipeline sent to a person with a proposal no person-overridable check blocks.</summary>
    private static (BankPayment Payment, QueueProposal Proposal) ConfirmableProposal(TwoTenants tenants, SandboxState state)
    {
        var found = tenants.Data.Payments
            .Where(p => p.Id != tenants.RejectedPaymentId && state.IsActionable(p.Id))
            .SelectMany(p => state.ProposalsFor(p).Select(q => (Payment: p, Proposal: q)))
            .FirstOrDefault(x => x.Proposal.Sources.Any(s => !s.ByAgent) && !x.Proposal.Checks.Any(ProposalChecks.Blocking.Contains));
        Assert.NotNull(found.Payment);
        return found;
    }

    [Fact]
    public async Task Confirming_a_pipeline_proposal_matches_its_invoices_and_records_the_person()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var db = tenants.For(tenants.A);
        var (payment, proposal) = ConfirmableProposal(tenants, await StateAsync(db));

        var result = await Actions(db).ConfirmAsync(payment.Id, proposal.Id, proposal.Checks.ToList(), Ct);

        Assert.Equal(ReviewOutcome.Done, result.Outcome);
        var state = await StateAsync(db);
        Assert.Equal(PaymentState.Matched, state.StateOf(payment.Id));
        var review = state.Review(payment.Id)!;
        Assert.Equal((ReviewAction.Confirmed, proposal.Id), (review.Action, review.ProposalId));
        Assert.Equal(proposal.InvoiceNumbers, review.InvoiceNumbers);
        Assert.Equal(await db.Users.Select(u => u.Id).SingleAsync(Ct), review.UserId);
        Assert.Contains(review.Proposals, p => p.Id == proposal.Id && p.ProposedBy.SequenceEqual(proposal.Sources.Select(s => s.ProposedBy)));
        Assert.All(proposal.InvoiceNumbers, n => Assert.Equal(payment.Id, state.MatchedInvoices[n]));
    }

    [Fact]
    public async Task Warnings_must_be_acknowledged_exactly_before_a_match_is_saved()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var db = tenants.For(tenants.A);
        var state = await StateAsync(db);
        var payment = tenants.Data.Payments.First(p => p.Id != tenants.RejectedPaymentId && state.IsActionable(p.Id));

        // An open invoice of another customer that the payment does not name: amount and reference do not fit.
        var stranger = state.OpenInvoices().First(i => state.Data.CustomerOfPayer(payment)?.Id != i.CustomerId && !state.Check(payment, [i.Number]).Any(ProposalChecks.Blocking.Contains));
        var preview = await Actions(db).PreviewAsync(payment.Id, [stranger.Number], Ct);
        Assert.Equal(ReviewOutcome.NeedsAcknowledgement, preview.Outcome);
        Assert.Contains(Rejections.NotIdentified, preview.Failures);

        Assert.Equal(ReviewOutcome.NeedsAcknowledgement, (await Actions(db).MatchManuallyAsync(payment.Id, [stranger.Number], [], Ct)).Outcome);
        Assert.Equal(ReviewOutcome.NeedsAcknowledgement, (await Actions(db).MatchManuallyAsync(payment.Id, [stranger.Number], preview.Failures.Skip(1).ToList(), Ct)).Outcome);
        Assert.Equal(ReviewOutcome.NeedsAcknowledgement, (await Actions(db).MatchManuallyAsync(payment.Id, [stranger.Number], [.. preview.Failures, Rejections.Ambiguous], Ct)).Outcome);
        Assert.Null((await StateAsync(db)).Review(payment.Id));

        var done = await Actions(db).MatchManuallyAsync(payment.Id, [stranger.Number], preview.Failures.Reverse().ToList(), Ct);

        Assert.Equal(ReviewOutcome.Done, done.Outcome);
        var review = (await StateAsync(db)).Review(payment.Id)!;
        Assert.Equal(ReviewAction.MatchedManually, review.Action);
        Assert.Equal(preview.Failures.Order(StringComparer.Ordinal), review.Acknowledged);
        Assert.Equal(preview.Failures, review.Checks);
    }

    [Fact]
    public async Task An_invoice_matched_already_blocks_whatever_the_person_acknowledges()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var db = tenants.For(tenants.A);
        var state = await StateAsync(db);
        var payment = tenants.Data.Payments.First(p => p.Id != tenants.RejectedPaymentId && state.IsActionable(p.Id));
        var taken = state.MatchedInvoices.Keys.First();

        var result = await Actions(db).MatchManuallyAsync(payment.Id, [taken], [Rejections.AlreadyMatched, Rejections.NotIdentified, Rejections.AmountMismatch, Rejections.PayerNotCustomer], Ct);

        Assert.Equal(ReviewOutcome.Blocked, result.Outcome);
        Assert.Contains(Rejections.AlreadyMatched, result.Failures);
        Assert.Null((await StateAsync(db)).Review(payment.Id));
    }

    [Fact]
    public async Task Bad_input_and_closed_payments_change_nothing()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var db = tenants.For(tenants.A);
        var state = await StateAsync(db);
        var actionable = tenants.Data.Payments.First(p => p.Id != tenants.RejectedPaymentId && state.IsActionable(p.Id));
        var auto = tenants.Data.Payments.First(p => state.Decision(p.Id)?.Decision.Outcome == Outcome.AutoMatched);
        var waiting = tenants.Data.Payments.First(p => state.StateOf(p.Id) == PaymentState.WaitingForAi);
        var other = tenants.Data.Payments.First(p => p.Id != actionable.Id && state.ProposalsFor(p).Count > 0);
        var open = state.OpenInvoices()[0].Number;
        var actions = Actions(db);

        Assert.Equal(ReviewOutcome.NotFound, (await actions.RejectAsync("P9999", Ct)).Outcome);
        Assert.Equal(ReviewOutcome.NotActionable, (await actions.RejectAsync(auto.Id, Ct)).Outcome);
        Assert.Equal(ReviewOutcome.NotActionable, (await actions.MatchManuallyAsync(waiting.Id, [open], [], Ct)).Outcome);
        Assert.Equal(ReviewOutcome.AlreadyDecided, (await actions.RejectAsync(tenants.RejectedPaymentId, Ct)).Outcome);
        Assert.Equal(ReviewOutcome.UnknownProposal, (await actions.ConfirmAsync(actionable.Id, "PR-000000000000", [], Ct)).Outcome);
        Assert.Equal(ReviewOutcome.UnknownProposal, (await actions.ConfirmAsync(actionable.Id, state.ProposalsFor(other)[0].Id, [], Ct)).Outcome);
        Assert.Equal(ReviewOutcome.InvalidInvoices, (await actions.MatchManuallyAsync(actionable.Id, [], [], Ct)).Outcome);
        Assert.Equal(ReviewOutcome.InvalidInvoices, (await actions.MatchManuallyAsync(actionable.Id, [open, open], [], Ct)).Outcome);
        Assert.Equal(ReviewOutcome.InvalidInvoices, (await actions.MatchManuallyAsync(actionable.Id, ["2099-0001"], [], Ct)).Outcome);
        Assert.Equal(ReviewOutcome.InvalidInvoices, (await actions.MatchManuallyAsync(actionable.Id, state.OpenInvoices().Take(5).Select(i => i.Number).ToList(), [], Ct)).Outcome);

        Assert.Equal(1, await db.Reviews.CountAsync(Ct));
    }

    [Fact]
    public async Task Rejecting_keeps_the_payment_unmatched_and_is_final()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var db = tenants.For(tenants.A);
        var (payment, proposal) = ConfirmableProposal(tenants, await StateAsync(db));
        var matchesBefore = await db.InvoiceMatches.CountAsync(Ct);

        Assert.Equal(ReviewOutcome.Done, (await Actions(db).RejectAsync(payment.Id, Ct)).Outcome);
        Assert.Equal(ReviewOutcome.AlreadyDecided, (await Actions(db).ConfirmAsync(payment.Id, proposal.Id, proposal.Checks.ToList(), Ct)).Outcome);

        var state = await StateAsync(db);
        Assert.Equal(PaymentState.Rejected, state.StateOf(payment.Id));
        Assert.Empty(state.Review(payment.Id)!.InvoiceNumbers);
        Assert.Equal(matchesBefore, await db.InvoiceMatches.CountAsync(Ct));
    }

    [Fact]
    public async Task Two_requests_at_once_decide_a_payment_once()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        BankPayment payment;
        QueueProposal proposal;
        await using (var db = tenants.For(tenants.A))
        {
            (payment, proposal) = ConfirmableProposal(tenants, await StateAsync(db));
        }

        await using var first = tenants.For(tenants.A);
        await using var second = tenants.For(tenants.A);
        var results = await Task.WhenAll(
            Actions(first).ConfirmAsync(payment.Id, proposal.Id, proposal.Checks.ToList(), Ct),
            Actions(second).RejectAsync(payment.Id, Ct));

        Assert.Single(results, r => r.Outcome == ReviewOutcome.Done);
        Assert.Single(results, r => r.Outcome == ReviewOutcome.AlreadyDecided);
        await using var check = tenants.For(tenants.A);
        Assert.Equal(1, await check.Reviews.CountAsync(r => r.PaymentId == payment.Id, Ct));
    }

    [Fact]
    public async Task A_person_acts_only_on_their_own_sandbox_even_with_the_same_ids()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        BankPayment payment;
        QueueProposal proposal;
        await using (var a = tenants.For(tenants.A))
        {
            (payment, proposal) = ConfirmableProposal(tenants, await StateAsync(a));
            Assert.Equal(ReviewOutcome.Done, (await Actions(a).ConfirmAsync(payment.Id, proposal.Id, proposal.Checks.ToList(), Ct)).Outcome);
        }

        // B has the same payment id, proposal id and invoice numbers: none of A's decision shows there.
        await using (var b = tenants.For(tenants.B))
        {
            var state = await StateAsync(b);
            Assert.Null(state.Review(payment.Id));
            Assert.True(state.IsActionable(payment.Id));
            Assert.All(proposal.InvoiceNumbers, n => Assert.False(state.MatchedInvoices.ContainsKey(n)));
            Assert.Contains(state.ProposalsFor(payment), p => p.Id == proposal.Id);
            Assert.Equal(1, await b.Reviews.CountAsync(Ct));

            // B can still decide its own row with the same ids.
            Assert.Equal(ReviewOutcome.Done, (await Actions(b).RejectAsync(payment.Id, Ct)).Outcome);
        }

        // A row that only exists in B is unknown to A, and so is an invoice that only B has.
        await using (var b = tenants.For(tenants.B))
        {
            b.Payments.Add(new PaymentRow { Id = "P9999", Seq = 99, Amount = 1, Currency = "CZK", CounterpartyIban = "", CounterpartyName = "B only", VariableSymbol = "", Message = "", RawLine = "P9999" });
            b.Invoices.Add(new InvoiceRow { Number = "2099-9999", VariableSymbol = "20999999", CustomerId = tenants.Data.Customers[0].Id, Kind = "standard", Amount = 1, Currency = "CZK" });
            await b.SaveChangesAsync(Ct);
        }

        await using (var a = tenants.For(tenants.A))
        {
            var state = await StateAsync(a);
            var actionable = tenants.Data.Payments.First(p => p.Id != tenants.RejectedPaymentId && state.IsActionable(p.Id));
            Assert.Equal(ReviewOutcome.NotFound, (await Actions(a).RejectAsync("P9999", Ct)).Outcome);
            Assert.Equal(ReviewOutcome.InvalidInvoices, (await Actions(a).MatchManuallyAsync(actionable.Id, ["2099-9999"], [], Ct)).Outcome);
            Assert.Equal(2, await a.Reviews.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task Agent_proposals_join_the_queue_next_to_the_pipeline_proposal()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var db = tenants.For(tenants.A);
        var (payment, pipeline) = ConfirmableProposal(tenants, await StateAsync(db));
        var other = (await StateAsync(db)).OpenInvoices().First(i => !pipeline.InvoiceNumbers.Contains(i.Number)).Number;
        foreach (var invoices in new[] { pipeline.InvoiceNumbers, [other] })
        {
            var proposal = new Proposal(Proposal.IdFor(payment.Id, invoices), payment.Id, invoices, "agent reason <b>", Proposal.ByMcpAgent, TwoTenants.Now, []);
            db.Proposals.Add(new ProposalRow { Id = proposal.Id, PaymentId = payment.Id, At = proposal.At, Record = Proposal.Serialize(proposal) });
        }

        await db.SaveChangesAsync(Ct);

        var proposals = (await StateAsync(db)).ProposalsFor(payment);

        var same = proposals.Single(p => p.Id == pipeline.Id);
        Assert.Equal([false, true], same.Sources.Select(s => s.ByAgent));
        Assert.Equal(Proposal.ByMcpAgent, same.Sources[1].ProposedBy);
        var agentOnly = proposals.Single(p => p.InvoiceNumbers.SequenceEqual([other]));
        Assert.Equal(Proposal.ByMcpAgent, Assert.Single(agentOnly.Sources).ProposedBy);
        Assert.Contains(Rejections.NotIdentified, agentOnly.Checks);
    }

    [Fact]
    public async Task A_later_pipeline_run_does_not_match_an_invoice_a_person_already_matched()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var db = tenants.For(tenants.A);
        var state = await StateAsync(db);
        var selector = new CandidateSelector(tenants.Data, MatchingOptions.Default);
        var unavailable = state.MatchedInvoices.Keys.ToHashSet(StringComparer.Ordinal);

        // A payment that waits for the AI run, and the invoice the code would offer it first.
        var (waiting, invoice) = tenants.Data.Payments
            .Where(p => state.StateOf(p.Id) == PaymentState.WaitingForAi)
            .Select(p => (Payment: p, Candidates: selector.Select(p, unavailable)))
            .Where(x => !x.Candidates.IsEmpty)
            .Select(x => (x.Payment, x.Candidates.Invoices[0].Invoice.Number))
            .First();

        // Meanwhile a person matches that invoice to another payment.
        var other = tenants.Data.Payments.First(p => p.Id != tenants.RejectedPaymentId && state.IsActionable(p.Id));
        var preview = await Actions(db).PreviewAsync(other.Id, [invoice], Ct);
        Assert.Equal(ReviewOutcome.Done, (await Actions(db).MatchManuallyAsync(other.Id, [invoice], preview.Failures, Ct)).Outcome);

        var store = await DbDecisionStore.LoadAsync(db, Ct);
        var gateway = new PaymentMatching.AiGateway.AiGateway(new NaiveModelStub(), ModelSettings.Default, PaymentMatching.AiGateway.PriceTable.Claude);
        var report = await new MatchingPipeline(tenants.Data, gateway, store, MatchingOptions.Default, new FixedTime(TwoTenants.Now)).RunAsync(Ct);
        await db.SaveChangesAsync(Ct);

        var decision = report.Records.Single(r => r.PaymentId == waiting.Id);
        Assert.False(decision.Decision.Outcome == Outcome.AutoMatched && decision.Decision.InvoiceNumbers.Contains(invoice));
        var after = await StateAsync(db);
        Assert.DoesNotContain(tenants.Data.Payments, p => after.StateOf(p.Id) == PaymentState.WaitingForAi);
        Assert.Equal(other.Id, after.MatchedInvoices[invoice]);
        Assert.Equal(PaymentState.Matched, after.StateOf(other.Id));
    }

    [Fact]
    public async Task Invoices_a_person_matched_are_taken_for_the_pipeline_too()
    {
        var tenants = await TwoTenants.CreateAsync(postgres);
        await using var db = tenants.For(tenants.A);
        var (payment, proposal) = ConfirmableProposal(tenants, await StateAsync(db));
        await Actions(db).ConfirmAsync(payment.Id, proposal.Id, proposal.Checks.ToList(), Ct);

        var store = await DbDecisionStore.LoadAsync(db, Ct);

        Assert.All(proposal.InvoiceNumbers, n =>
        {
            Assert.Contains(n, store.MatchedInvoices);
            Assert.Equal(payment.Id, store.HumanMatches[n]);
        });
    }
}
