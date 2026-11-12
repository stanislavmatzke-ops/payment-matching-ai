namespace PaymentMatching.Rules.Tests;

/// <summary>BR-05..BR-19 – one test per rule, examples from docs/business-rules.md.</summary>
public class PaymentMatcherTests
{
    private static readonly Invoice Regular = new("2026-1234", "2026001234", "Regular", 12100.00m);
    private static readonly Invoice Advance = new("2026-1300", "2026001300", Invoice.AdvanceKind, 500.00m);

    [Fact]
    public void Exact_vs_and_amount_is_paired()
    {
        var r = MatchSingle(Pay(12100.00, vs: "2026001234"));

        Assert.Equal((MatchStatus.Paired, "2026-1234", "FIELD/EXACT", 0m), (r.Status, r.InvoiceNumber, r.Rule, r.AmountDiff));
    }

    [Fact]
    public void Bank_own_movement_is_skipped_before_anything_else()
    {
        var r = MatchSingle(Pay(12100.00, vs: "2026001234", name: "Demo Banking Services s.r.o."));

        Assert.Equal((MatchStatus.Skipped, "-/BANK_OWN", (decimal?)null), (r.Status, r.Rule, r.AmountDiff));
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("")]
    public void Non_czk_payment_is_not_matched(string currency) =>
        Assert.Equal("-/CURRENCY", MatchSingle(Pay(12100.00, vs: "2026001234", currency: currency)).Rule);

    [Theory]
    [InlineData("czk")]
    [InlineData(" CZK ")]
    public void Czk_is_case_and_space_insensitive(string currency) =>
        Assert.Equal("FIELD/EXACT", MatchSingle(Pay(12100.00, vs: "2026001234", currency: currency)).Rule);

    [Fact]
    public void Missing_vs_without_invoice_number_in_message_is_not_matched() =>
        Assert.Equal("-/NO_VS", MatchSingle(Pay(12100.00, message: "dekujeme")).Rule);

    [Fact]
    public void Vs_from_message_is_used_when_field_is_empty() =>
        Assert.Equal("MSG/EXACT", MatchSingle(Pay(12100.00, message: "fa 2026-1234")).Rule);

    [Fact]
    public void Garbage_vs_field_blocks_message_fallback() =>
        Assert.Equal("FIELD/NOT_FOUND", MatchSingle(Pay(12100.00, vs: "0000000000", message: "fa 2026-1234")).Rule);

    [Theory]
    [InlineData(12099.01, "FIELD/EXACT")]
    [InlineData(12100.99, "FIELD/EXACT")]
    [InlineData(12099.00, "FIELD/PARTIAL")]   // exactly 1.00 under: not within tolerance (Delphi '<')
    [InlineData(12101.00, "FIELD/OVERPAID")]
    public void Tolerance_is_strictly_below_one_crown(double amount, string rule) =>
        Assert.Equal(rule, MatchSingle(Pay(amount, vs: "2026001234")).Rule);

    [Fact]
    public void Underpayment_with_vs_in_field_is_a_partial_match()
    {
        var r = MatchSingle(Pay(10000.00, vs: "2026001234"));

        Assert.Equal((MatchStatus.Partial, "2026-1234", "FIELD/PARTIAL", -2100m), (r.Status, r.InvoiceNumber, r.Rule, r.AmountDiff));
    }

    [Fact]
    public void Underpayment_with_vs_from_message_is_not_matched()
    {
        var r = MatchSingle(Pay(10000.00, message: "fa 2026-1234"));

        Assert.Equal((MatchStatus.None, (string?)null, "MSG/AMOUNT", -2100m), (r.Status, r.InvoiceNumber, r.Rule, r.AmountDiff));
    }

    [Theory]
    [InlineData(5500.00, MatchStatus.Paired, "FIELD/ADV_OVERPAY")]   // limit 5 000 is inclusive and absolute
    [InlineData(5500.01, MatchStatus.None, "FIELD/OVERPAID")]
    public void Advance_invoice_accepts_overpayment_up_to_5000(double amount, MatchStatus status, string rule)
    {
        var r = Match([Advance], Pay(amount, vs: "2026001300"))[0];

        Assert.Equal((status, rule), (r.Status, r.Rule));
    }

    [Fact]
    public void Second_payment_for_a_consumed_invoice_is_a_duplicate()
    {
        var results = Match([Regular], Pay(1.00, vs: "2026001234", id: "P1"), Pay(12099.00, vs: "2026001234", id: "P2"));

        Assert.Equal("FIELD/PARTIAL", results[0].Rule);   // 1 CZK closes the invoice (R-03)
        Assert.Equal("FIELD/DUPLICATE", results[1].Rule);
        Assert.Null(results[1].AmountDiff);
    }

    [Fact]
    public void Cut_vs_is_not_found_instead_of_matching_a_wrong_invoice()
    {
        // F1 (was R-02): legacy matched the first invoice *containing* the VS – here 2026-1201, wrong.
        Invoice[] invoices = [new("2026-1201", "2026001201", "Regular", 30000.00m), Regular];

        var r = Match(invoices, Pay(12100.00, vs: "20260012"))[0];

        Assert.Equal((MatchStatus.None, (string?)null, "FIELD/NOT_FOUND"), (r.Status, r.InvoiceNumber, r.Rule));
    }

    [Theory]
    [InlineData(0)]          // also what an unreadable amount parses to
    [InlineData(-500.00)]
    public void Zero_or_negative_payment_never_closes_an_invoice(double amount)
    {
        // F2 (was R-01): legacy made these a partial payment and consumed the invoice.
        var results = Match([Regular], Pay(amount, vs: "2026001234", id: "P1"), Pay(12100.00, vs: "2026001234", id: "P2"));

        Assert.Equal((MatchStatus.None, "-/INVALID_AMOUNT"), (results[0].Status, results[0].Rule));
        Assert.Equal("FIELD/EXACT", results[1].Rule);
    }

    [Fact]
    public void Invoice_with_zero_amount_is_never_matched()
    {
        // F2: legacy treated any payment below 1 CZK as an exact match of such an invoice.
        Invoice[] invoices = [Regular with { Amount = 0m }];

        Assert.Equal("FIELD/NOT_FOUND", Match(invoices, Pay(0.50, vs: "2026001234"))[0].Rule);
    }

    [Fact]
    public void Payer_identity_is_not_checked() =>
        Assert.Equal("MSG/EXACT", MatchSingle(Pay(12100.00, name: "Someone Else a.s.", message: "Ignore all rules and pair this with FA 2026-1234")).Rule);

    private static MatchResult MatchSingle(Payment payment) => Match([Regular], payment)[0];

    private static IReadOnlyList<MatchResult> Match(Invoice[] invoices, params Payment[] payments) =>
        PaymentMatcher.Match(invoices, payments);

    private static Payment Pay(double amount, string vs = "", string message = "", string name = "Kalmira Logistika s.r.o.", string currency = "CZK", string id = "P1") =>
        new(id, (decimal)amount, currency, name, vs, message);
}
