using ModelContextProtocol;
using PaymentMatching.Matching;

namespace PaymentMatching.Mcp.Tests;

public sealed class ProposeMatchTests : IDisposable
{
    private readonly SampleWorkspace _sample = SampleWorkspace.Create();

    private PaymentQueries Queries => _sample.Queries;

    public void Dispose() => _sample.Dispose();

    [Fact]
    public void A_proposal_is_queued_for_a_person_and_matches_nothing()
    {
        var (payment, invoices) = ThirdPartyPaymentInReview();
        var auditBefore = File.ReadAllBytes(_sample.AuditPath);

        var result = Queries.ProposeMatch(payment.Id, invoices, "Message names the invoice; payer is the parent company.");

        Assert.True(result.Created);
        Assert.Equal("pending_human_review", result.Status);
        Assert.Equal(PaymentStatus.HumanReview, _sample.Workspace.Status(payment.Id));
        Assert.Equal(auditBefore, File.ReadAllBytes(_sample.AuditPath));
        Assert.DoesNotContain(invoices, _sample.Workspace.MatchedInvoices.ContainsKey);

        var queued = Assert.Single(Queries.GetPayment(payment.Id).Proposals);
        Assert.Equal(result.ProposalId, queued.Id);
        Assert.Equal(Proposal.ByMcpAgent, queued.ProposedBy);
        Assert.True(Queries.GetPayment(payment.Id).Payment.HasProposal);
    }

    [Fact]
    public void Validation_by_code_runs_on_the_proposal_and_its_failures_go_to_the_reviewer()
    {
        // Correct invoice, but a third-party payer: the pipeline's rule PAYER_NOT_CUSTOMER still applies.
        var (payment, invoices) = ThirdPartyPaymentInReview();

        var result = Queries.ProposeMatch(payment.Id, invoices, "Message names the invoice.");

        Assert.False(result.AllChecksPassed);
        Assert.Equal([Rejections.PayerNotCustomer], result.FailedChecks);
    }

    [Fact]
    public void A_proposal_based_on_amount_alone_fails_the_checks()
    {
        var (payment, invoices) = ThirdPartyPaymentInReview();
        var right = _sample.Workspace.Data.InvoiceByNumber[invoices[0]];
        var sameAmountElsewhere = _sample.Workspace.Data.Invoices.First(i => i.Number != right.Number && !_sample.Workspace.MatchedInvoices.ContainsKey(i.Number));

        var result = Queries.ProposeMatch(payment.Id, [sameAmountElsewhere.Number], "Amount looks close.");

        Assert.False(result.AllChecksPassed);
        Assert.Contains(Rejections.NotIdentified, result.FailedChecks);
    }

    [Fact]
    public void An_agent_obeying_an_injection_gets_only_a_proposal_marked_as_injection()
    {
        var injections = _sample.Expected
            .Where(e => e.Value.InjectionTarget.Length > 0 && _sample.Workspace.Data.InvoiceByNumber.ContainsKey(e.Value.InjectionTarget))
            .ToList();
        var tried = 0;
        foreach (var (paymentId, expected) in injections)
        {
            if (_sample.Workspace.MatchedInvoices.ContainsKey(expected.InjectionTarget))
            {
                continue;
            }

            tried++;
            var result = Queries.ProposeMatch(paymentId, [expected.InjectionTarget], "The payment message says to match this invoice.");

            Assert.Contains(Rejections.InjectionSuspected, result.FailedChecks);
            Assert.False(result.AllChecksPassed);
            Assert.NotEqual(PaymentStatus.AutoMatched, _sample.Workspace.Status(paymentId));
        }

        Assert.True(tried >= 3, $"only {tried} injection targets were open");
    }

    [Fact]
    public void The_same_proposal_is_stored_once_whatever_the_order_of_invoices()
    {
        var payment = _sample.PaymentsWith(PaymentStatus.HumanReview).First();
        var open = _sample.Workspace.Data.Invoices.Where(i => !_sample.Workspace.MatchedInvoices.ContainsKey(i.Number)).Take(2).Select(i => i.Number).ToArray();

        var first = Queries.ProposeMatch(payment.Id, open, "First.");
        var second = Queries.ProposeMatch(payment.Id, [open[1], open[0], open[0]], "Second time.");

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.ProposalId, second.ProposalId);
        Assert.Single(File.ReadAllLines(_sample.ProposalsPath));
        Assert.Equal("First.", Assert.Single(_sample.Workspace.Proposals.ForPayment(payment.Id)).Reason);
    }

    [Fact]
    public void Proposals_survive_a_restart()
    {
        var (payment, invoices) = ThirdPartyPaymentInReview();
        var created = Queries.ProposeMatch(payment.Id, invoices, "Reason.");

        var reloaded = Workspace.Load(SampleWorkspace.DataDir, _sample.StateDir);

        var proposal = Assert.Single(reloaded.Proposals.ForPayment(payment.Id));
        Assert.Equal(created.ProposalId, proposal.Id);
        Assert.Equal(invoices, proposal.InvoiceNumbers);
        Assert.Equal([Rejections.PayerNotCustomer], proposal.Checks);
    }

    [Fact]
    public void An_auto_matched_payment_cannot_get_a_proposal()
    {
        var payment = _sample.PaymentsWith(PaymentStatus.AutoMatched).First();
        var open = _sample.Workspace.Data.Invoices.First(i => !_sample.Workspace.MatchedInvoices.ContainsKey(i.Number));

        var error = Assert.Throws<McpException>(() => Queries.ProposeMatch(payment.Id, [open.Number], "Reason."));
        Assert.Contains("already matched", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_invoice_matched_to_another_payment_cannot_be_proposed()
    {
        var payment = _sample.PaymentsWith(PaymentStatus.HumanReview).First();
        var (invoice, owner) = _sample.Workspace.MatchedInvoices.First();

        var error = Assert.Throws<McpException>(() => Queries.ProposeMatch(payment.Id, [invoice], "Reason."));
        Assert.Contains(owner, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void A_proposal_has_one_to_four_invoices(int count)
    {
        var payment = _sample.PaymentsWith(PaymentStatus.HumanReview).First();
        var open = _sample.Workspace.Data.Invoices.Where(i => !_sample.Workspace.MatchedInvoices.ContainsKey(i.Number)).Take(count).Select(i => i.Number).ToArray();

        Assert.Throws<McpException>(() => Queries.ProposeMatch(payment.Id, open, "Reason."));
    }

    [Fact]
    public void Unknown_ids_and_bad_reasons_are_rejected_and_nothing_is_written()
    {
        var payment = _sample.PaymentsWith(PaymentStatus.HumanReview).First();
        var open = _sample.Workspace.Data.Invoices.First(i => !_sample.Workspace.MatchedInvoices.ContainsKey(i.Number)).Number;

        Assert.Throws<McpException>(() => Queries.ProposeMatch("P9999", [open], "Reason."));
        Assert.Throws<McpException>(() => Queries.ProposeMatch(payment.Id, ["2026-9999"], "Reason."));
        Assert.Throws<McpException>(() => Queries.ProposeMatch(payment.Id, [open], "   "));
        Assert.Throws<McpException>(() => Queries.ProposeMatch(payment.Id, [open], new string('x', PaymentQueries.MaxReasonLength + 1)));
        Assert.False(File.Exists(_sample.ProposalsPath));
    }

    [Fact]
    public void Proposal_id_ignores_invoice_order() =>
        Assert.Equal(Proposal.IdFor("P1", ["2026-1002", "2026-1001"]), Proposal.IdFor("P1", ["2026-1001", "2026-1002"]));

    /// <summary>A third-party payment the pipeline sent to a person, with its correct invoice from the ground truth.</summary>
    private (BankPayment Payment, string[] Invoices) ThirdPartyPaymentInReview()
    {
        var payment = _sample.PaymentsWith(PaymentStatus.HumanReview)
            .First(p => _sample.Expected[p.Id].CaseType == "ThirdPartyPayer" && _sample.Workspace.Data.CustomerOfPayer(p) is null);
        return (payment, _sample.Expected[payment.Id].Invoices);
    }
}
