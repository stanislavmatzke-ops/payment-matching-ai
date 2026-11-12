using PaymentMatching.AiGateway;
using static PaymentMatching.Matching.Tests.TestData;

namespace PaymentMatching.Matching.Tests;

public class ValidationAndDecisionTests
{
    private static readonly InvoiceRecord A1 = Invoice("2026-1001", 100m);
    private static readonly InvoiceRecord A2 = Invoice("2026-1002", 250m);
    private static readonly InvoiceRecord B1 = Invoice("2026-2001", 250m, "C002");

    private static ValidationResult Validate(BankPayment payment, string[] proposal, HashSet<string>? unavailable = null, bool injection = false)
    {
        var data = Dataset([A1, A2, B1], payment);
        var selector = new CandidateSelector(data, MatchingOptions.Default);
        // All invoices are candidates "named with a typo" (identified, but not required), so each check is tested on its own.
        var all = new CandidateSet(data.Invoices.Select(i => new Candidate(i, [Signals.VsOneTypo], 0)).ToList(), []);
        return new ProposalValidator(data, selector, MatchingOptions.Default).Validate(payment, proposal, all, unavailable ?? [], injection);
    }

    [Fact]
    public void Correct_multi_invoice_proposal_passes()
    {
        Assert.True(Validate(Payment("P1", 350m), ["2026-1001", "2026-1002"]).IsValid);
    }

    [Fact]
    public void Proposal_must_include_the_invoice_the_payment_names_and_amount_alone_identifies_nothing()
    {
        var payment = Payment("P1", 250m, vs: "2026001001");
        var data = Dataset([A1, A2], payment);
        var selector = new CandidateSelector(data, MatchingOptions.Default);
        var named = new Candidate(A1, [Signals.VsExact], 100);
        var amountOnly = new Candidate(A2, [Signals.AmountExact, Signals.PayerIsCustomer], 70);
        var set = new CandidateSet([named, amountOnly], []);

        var result = new ProposalValidator(data, selector, MatchingOptions.Default).Validate(payment, ["2026-1002"], set, new HashSet<string>(), false);

        Assert.Contains(Rejections.NotIdentified, result.Failures);
    }

    [Fact]
    public void Eur_payment_may_pay_one_invoice_only()
    {
        var selector = new CandidateSelector(Dataset([A1, A2]), MatchingOptions.Default);
        var eur = Payment("P1", 14m, currency: "EUR"); // 350 CZK / 14 EUR = 25.00

        Assert.True(selector.AmountFits(eur with { Amount = 4m }, [A1]));
        Assert.False(selector.AmountFits(eur, [A1, A2]));
    }

    [Fact]
    public void Proposal_outside_the_candidates_is_rejected()
    {
        var payment = Payment("P1", 100m);
        var data = Dataset([A1, A2], payment);
        var selector = new CandidateSelector(data, MatchingOptions.Default);
        var onlyA2 = new CandidateSet([new Candidate(A2, [], 0)], []);

        var result = new ProposalValidator(data, selector, MatchingOptions.Default).Validate(payment, ["2026-1001"], onlyA2, new HashSet<string>(), false);

        Assert.Equal([Rejections.NotACandidate], result.Failures);
    }

    [Theory]
    [InlineData(349.00, Rejections.AmountMismatch)] // partial payment
    [InlineData(351.00, Rejections.AmountMismatch)] // overpayment
    public void Amount_must_fit(decimal amount, string failure)
    {
        Assert.Contains(failure, Validate(Payment("P1", amount), ["2026-1001", "2026-1002"]).Failures);
    }

    [Fact]
    public void Each_check_has_its_own_reason()
    {
        Assert.Contains(Rejections.NoInvoices, Validate(Payment("P1", 100m), []).Failures);
        Assert.Contains(Rejections.AlreadyMatched, Validate(Payment("P1", 100m), ["2026-1001"], ["2026-1001"]).Failures);
        Assert.Contains(Rejections.MixedCustomers, Validate(Payment("P1", 350m), ["2026-1001", "2026-2001"]).Failures);
        Assert.Contains(Rejections.PayerNotCustomer, Validate(Payment("P1", 100m, iban: StrangerIban), ["2026-1001"]).Failures);
        Assert.Contains(Rejections.InjectionSuspected, Validate(Payment("P1", 100m), ["2026-1001"], injection: true).Failures);
    }

    private static GatewayResult<AiMatchAnswer> Result(string decision, string[] invoices, double confidence, params string[] flags) =>
        new(AiMatchAnswer.Parse(FakeModel.Answer(decision, invoices, confidence, flags)), "claude-haiku-5-5", [], false, false);

    private static readonly ValidationResult Valid = new([]);

    [Fact]
    public void Valid_confident_match_is_automatic()
    {
        var decision = new AiDecisionMaker(MatchingOptions.Default).Decide(Payment("P1", 100m), Result("match", ["2026-1001"], 0.95), Valid, true, false);

        Assert.Equal(Outcome.AutoMatched, decision.Outcome);
        Assert.Equal("ai:claude-haiku-5-5", decision.DecidedBy);
    }

    [Theory]
    [InlineData("match", 0.80)]
    [InlineData("uncertain", 0.99)]
    public void Low_confidence_or_uncertain_goes_to_a_person_with_the_proposal(string kind, double confidence)
    {
        var decision = new AiDecisionMaker(MatchingOptions.Default).Decide(Payment("P1", 100m), Result(kind, ["2026-1001"], confidence), Valid, true, false);

        Assert.Equal(Outcome.HumanReview, decision.Outcome);
        Assert.Equal(["2026-1001"], decision.InvoiceNumbers);
    }

    [Theory]
    [InlineData(Flags.ThirdPartyPayer)]
    [InlineData(Flags.PartialPayment)]
    [InlineData(Flags.Overpayment)]
    public void Risk_flags_always_need_a_person(string flag)
    {
        var decision = new AiDecisionMaker(MatchingOptions.Default).Decide(Payment("P1", 100m), Result("match", ["2026-1001"], 0.99, flag), Valid, true, false);

        Assert.Equal(Outcome.HumanReview, decision.Outcome);
    }

    [Fact]
    public void Failed_validation_is_never_automatic_and_explains_why()
    {
        var invalid = new ValidationResult([Rejections.AmountMismatch]);

        var decision = new AiDecisionMaker(MatchingOptions.Default).Decide(Payment("P1", 100m), Result("match", ["2026-1001"], 1.0), invalid, true, false);

        Assert.Equal(Outcome.HumanReview, decision.Outcome);
        Assert.Contains(Rejections.AmountMismatch, decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Suspected_injection_is_not_matched_and_the_target_is_not_proposed()
    {
        var maker = new AiDecisionMaker(MatchingOptions.Default);

        var byDetector = maker.Decide(Payment("P1", 100m), Result("match", ["2026-1001"], 1.0), Valid, true, injectionDetected: true);
        var byModel = maker.Decide(Payment("P1", 100m), Result("no_match", [], 0.99, Flags.PromptInjection), null, true, false);

        Assert.All(new[] { byDetector, byModel }, d =>
        {
            Assert.Equal(Outcome.NoMatch, d.Outcome);
            Assert.Empty(d.InvoiceNumbers);
            Assert.Contains(Flags.PromptInjection, d.Flags);
        });
    }

    [Fact]
    public void No_usable_answer_goes_to_a_person()
    {
        var empty = new GatewayResult<AiMatchAnswer>(null, null, [], true, BudgetExhausted: true);

        var decision = new AiDecisionMaker(MatchingOptions.Default).Decide(Payment("P1", 100m), empty, null, false, false);

        Assert.Equal(Outcome.HumanReview, decision.Outcome);
        Assert.Contains("budget", decision.Reason, StringComparison.Ordinal);
    }
}
