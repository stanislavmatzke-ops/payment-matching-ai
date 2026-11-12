using PaymentMatching.AiGateway;
using static PaymentMatching.Matching.Tests.TestData;

namespace PaymentMatching.Matching.Tests;

/// <summary>Phase 5: ties go to a person (found by the eval gold set), and escalation only when it can change the outcome.</summary>
public class AmbiguityAndEscalationTests
{
    // A monthly fee: two invoices of one customer with the same amount, VS …1234 and …1235.
    private static readonly InvoiceRecord January = Invoice("2026-1234", 500m);
    private static readonly InvoiceRecord February = Invoice("2026-1235", 500m);

    // The last VS digit is missing: one typo away from both invoices.
    private static readonly BankPayment Tied = Payment("P1", 500m, vs: "202600123");

    [Fact]
    public void A_proposal_is_ambiguous_when_another_candidate_passes_the_same_checks()
    {
        var data = Dataset([January, February], Tied);
        var selector = new CandidateSelector(data, MatchingOptions.Default);
        var validator = new ProposalValidator(data, selector, MatchingOptions.Default);
        var candidates = selector.Select(Tied, new HashSet<string>());

        Assert.Equal(2, candidates.Invoices.Count);
        Assert.Equal([Rejections.Ambiguous], validator.Validate(Tied, ["2026-1234"], candidates, new HashSet<string>(), false).Failures);
        Assert.Equal([Rejections.Ambiguous], validator.Validate(Tied, ["2026-1235"], candidates, new HashSet<string>(), false).Failures);
        Assert.False(validator.AnyValidProposal(Tied, candidates, new HashSet<string>()));
    }

    [Fact]
    public void Two_combinations_with_the_same_total_are_ambiguous()
    {
        var named = Invoice("2026-1001", 100m);
        var second = Invoice("2026-1002", 250m);
        var twin = Invoice("2026-1003", 250m);
        var payment = Payment("P1", 350m, vs: "2026001001", message: "úhrada faktur 2026-1001 a další");
        var data = Dataset([named, second, twin], payment);
        var selector = new CandidateSelector(data, MatchingOptions.Default);
        var candidates = selector.Select(payment, new HashSet<string>());

        var result = new ProposalValidator(data, selector, MatchingOptions.Default).Validate(payment, ["2026-1001", "2026-1002"], candidates, new HashSet<string>(), false);

        Assert.Equal(2, candidates.Combinations.Count);
        Assert.Equal([Rejections.Ambiguous], result.Failures);
    }

    [Fact]
    public async Task Confident_model_answer_on_a_tie_goes_to_a_person()
    {
        var model = new FakeModel(_ => FakeModel.Answer("match", ["2026-1234"], 0.99, Flags.VsTypo));

        var report = await Run(Dataset([January, February], Tied), model, MatchingOptions.Default);

        var decision = report.Records.Single().Decision;
        Assert.Equal(Outcome.HumanReview, decision.Outcome);
        Assert.Contains(Rejections.Ambiguous, report.Records.Single().ValidationFailures);
    }

    [Fact]
    public async Task No_escalation_when_no_answer_could_be_matched_automatically()
    {
        // Overpaid by 100 CZK: whatever a model says, validation fails on the amount, so a person decides.
        var overpaid = Payment("P1", 600m, vs: "2026001243");
        var model = new FakeModel(_ => FakeModel.Answer("uncertain", ["2026-1234"], 0.6, Flags.Overpayment));
        var options = MatchingOptions.Default with { EscalateOnlyWhenAutoMatchPossible = true };

        var report = await Run(Dataset([January], overpaid), model, options);

        var record = report.Records.Single();
        Assert.Equal(["small"], record.Calls.Select(c => c.Tier));
        Assert.Equal(Outcome.HumanReview, record.Decision.Outcome);
        Assert.Equal(["2026-1234"], record.Decision.InvoiceNumbers);
    }

    [Fact]
    public async Task Escalation_still_happens_when_an_automatic_match_is_possible()
    {
        var typo = Payment("P1", 500m, vs: "2026001243");
        var model = new FakeModel(r => r.Model == "claude-haiku-5-5"
            ? FakeModel.Answer("uncertain", ["2026-1234"], 0.6)
            : FakeModel.Answer("match", ["2026-1234"], 0.96, Flags.VsTypo));
        var options = MatchingOptions.Default with { EscalateOnlyWhenAutoMatchPossible = true };

        var report = await Run(Dataset([January], typo), model, options);

        var record = report.Records.Single();
        Assert.Equal(["small", "large"], record.Calls.Select(c => c.Tier));
        Assert.Equal(Outcome.AutoMatched, record.Decision.Outcome);
    }

    private static Task<RunReport> Run(MatchingDataset data, ILlmProvider model, MatchingOptions options) =>
        new MatchingPipeline(data, Gateway(model), new DecisionLog(), options).RunAsync(TestContext.Current.CancellationToken);
}
