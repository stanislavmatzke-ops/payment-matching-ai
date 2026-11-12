using PaymentMatching.Rules;
using static PaymentMatching.Matching.Tests.TestData;

namespace PaymentMatching.Matching.Tests;

public class RuleOutcomePolicyTests
{
    private static readonly InvoiceRecord Inv = Invoice("2026-1234", 12100m);

    private static PolicyResult Apply(MatchResult rule, string iban = AlfaIban, MatchingOptions? options = null)
    {
        var payment = Payment("P1", 12100m, iban: iban);
        return new RuleOutcomePolicy(Dataset([Inv], payment), options ?? MatchingOptions.Default).Apply(payment, rule);
    }

    private static MatchResult Rule(MatchStatus status, string rule, string? invoice = "2026-1234", decimal? diff = 0) =>
        new("P1", status, invoice, rule, diff);

    [Fact]
    public void Exact_vs_in_the_field_is_matched_by_the_rule_even_from_an_unknown_account()
    {
        var result = Apply(Rule(MatchStatus.Paired, "FIELD/EXACT"), iban: StrangerIban);

        Assert.Equal(Outcome.AutoMatched, result.Decision!.Outcome);
        Assert.Equal("rule:FIELD/EXACT", result.Decision.DecidedBy);
        Assert.Equal(["2026-1234"], result.Decision.InvoiceNumbers);
    }

    [Fact]
    public void Invoice_number_from_the_message_is_matched_when_the_payer_is_the_customer()
    {
        Assert.Equal(Outcome.AutoMatched, Apply(Rule(MatchStatus.Paired, "MSG/EXACT")).Decision!.Outcome);
    }

    [Theory]
    [InlineData(StrangerIban)]
    [InlineData(BetaIban)]
    public void Q07_message_match_from_another_account_goes_to_a_person(string iban)
    {
        var decision = Apply(Rule(MatchStatus.Paired, "MSG/EXACT"), iban).Decision!;

        Assert.Equal(Outcome.HumanReview, decision.Outcome);
        Assert.Equal(["2026-1234"], decision.InvoiceNumbers);
        Assert.Contains(Flags.ThirdPartyPayer, decision.Flags);
    }

    [Fact]
    public void Q02_partial_payment_goes_to_a_person_with_a_proposal()
    {
        var decision = Apply(Rule(MatchStatus.Partial, "FIELD/PARTIAL", diff: -2100m)).Decision!;

        Assert.Equal(Outcome.HumanReview, decision.Outcome);
        Assert.Equal(["2026-1234"], decision.InvoiceNumbers);
        Assert.Contains(Flags.PartialPayment, decision.Flags);
        Assert.Contains("12100.00 of 12100.00", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Q02_advance_overpayment_goes_to_a_person()
    {
        var decision = Apply(Rule(MatchStatus.Paired, "FIELD/ADV_OVERPAY", diff: 300m)).Decision!;

        Assert.Equal(Outcome.HumanReview, decision.Outcome);
        Assert.Contains(Flags.Overpayment, decision.Flags);
    }

    [Fact]
    public void Legacy_behaviour_can_be_restored_by_configuration()
    {
        var legacy = MatchingOptions.Default with { PartialPaymentsNeedHuman = false, RequireCustomerPayer = false };

        Assert.Equal(Outcome.AutoMatched, Apply(Rule(MatchStatus.Partial, "FIELD/PARTIAL"), options: legacy).Decision!.Outcome);
        Assert.Equal(Outcome.AutoMatched, Apply(Rule(MatchStatus.Paired, "MSG/EXACT"), StrangerIban, legacy).Decision!.Outcome);
    }

    [Fact]
    public void Bank_movement_is_not_matched()
    {
        var decision = Apply(Rule(MatchStatus.Skipped, "-/BANK_OWN", invoice: null, diff: null)).Decision!;

        Assert.Equal(Outcome.NoMatch, decision.Outcome);
    }

    [Fact]
    public void Invalid_amount_goes_to_a_person_without_calling_ai()
    {
        var result = Apply(Rule(MatchStatus.None, "-/INVALID_AMOUNT", invoice: null, diff: null));

        Assert.Equal(NextStep.Decided, result.Next);
        Assert.Equal(Outcome.HumanReview, result.Decision!.Outcome);
    }

    [Theory]
    [InlineData("FIELD/NOT_FOUND")]
    [InlineData("FIELD/OVERPAID")]
    [InlineData("MSG/OVERPAID")]
    [InlineData("MSG/AMOUNT")]
    [InlineData("MSG/DUPLICATE")]
    [InlineData("-/NO_VS")]
    [InlineData("-/CURRENCY")]
    public void Everything_the_rules_cannot_decide_goes_to_layer_2(string rule)
    {
        var result = Apply(Rule(MatchStatus.None, rule, invoice: null));

        Assert.Equal(NextStep.AskAi, result.Next);
        Assert.Null(result.Decision);
    }
}
