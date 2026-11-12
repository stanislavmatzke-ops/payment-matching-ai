using static PaymentMatching.Matching.Tests.TestData;

namespace PaymentMatching.Matching.Tests;

public class CandidateSelectorTests
{
    private static readonly HashSet<string> None = [];

    private static CandidateSet Select(IReadOnlyList<InvoiceRecord> invoices, BankPayment payment, IReadOnlySet<string>? unavailable = null)
    {
        var data = Dataset(invoices, payment);
        return new CandidateSelector(data, MatchingOptions.Default).Select(payment, unavailable ?? None);
    }

    [Theory]
    [InlineData("fa 2026-1234 díky")]
    [InlineData("Faktura č. 2026/1234")]
    [InlineData("uhrada FA20261234")]
    [InlineData("VS 2026001234")]
    public void Invoice_number_in_the_message_in_any_format(string message)
    {
        Assert.Contains("2026-1234", InvoiceReferences.InMessage(message));
    }

    [Theory]
    [InlineData("2026001234", "2026001243", true)] // adjacent swap
    [InlineData("2026001234", "202600134", true)] // missing digit
    [InlineData("2026001234", "2026001235", true)] // wrong digit
    [InlineData("2026001234", "2026001432", false)] // swap of non-adjacent digits
    [InlineData("2026001234", "2026001234", false)] // equal is not a typo
    [InlineData("2026001234", "20260012", false)]
    public void One_typo_apart(string a, string b, bool expected)
    {
        Assert.Equal(expected, InvoiceReferences.IsOneTypoApart(a, b));
    }

    [Fact]
    public void Vs_typo_selects_the_intended_invoice()
    {
        var set = Select([Invoice("2026-1234", 500m), Invoice("2026-1500", 700m)], Payment("P1", 500m, vs: "2026001243"));

        var candidate = Assert.Single(set.Invoices);
        Assert.Equal("2026-1234", candidate.Invoice.Number);
        Assert.Contains(Signals.VsOneTypo, candidate.Signals);
        Assert.Contains(Signals.AmountExact, candidate.Signals);
    }

    [Fact]
    public void Multi_invoice_payment_gets_a_combination_of_one_customer()
    {
        var invoices = new[] { Invoice("2026-1001", 100m), Invoice("2026-1002", 250m), Invoice("2026-1003", 999m), Invoice("2026-1004", 100m, "C002") };

        var set = Select(invoices, Payment("P1", 350m, message: "úhrada faktur 2026-1001 a další"));

        Assert.Equal([["2026-1001", "2026-1002"]], set.Combinations);
        Assert.DoesNotContain(set.Invoices, c => c.Invoice.Number is "2026-1003" or "2026-1004");
    }

    [Fact]
    public void Eur_payment_fits_a_czk_invoice_within_the_rate_band()
    {
        var set = Select([Invoice("2026-1234", 25_000m), Invoice("2026-1300", 30_000m)], Payment("P1", 1000m, vs: "2026001234", currency: "EUR"));

        var candidate = Assert.Single(set.Invoices);
        Assert.Contains(Signals.AmountFx, candidate.Signals);
    }

    [Fact]
    public void Matched_invoices_are_never_candidates()
    {
        var set = Select([Invoice("2026-1234", 500m)], Payment("P1", 500m, vs: "2026001234"), new HashSet<string> { "2026-1234" });

        Assert.True(set.IsEmpty);
    }

    [Fact]
    public void Unrelated_payment_from_an_unknown_account_has_no_candidates()
    {
        var set = Select([Invoice("2026-1234", 500m)], Payment("P1", 77.70m, vs: "9100001", message: "Vrácení přeplatku", iban: StrangerIban));

        Assert.True(set.IsEmpty);
    }

    [Fact]
    public void At_most_n_candidates_reach_the_model()
    {
        var invoices = Enumerable.Range(1000, 30).Select(n => Invoice($"2026-{n}", 500m)).ToList();

        var set = Select(invoices, Payment("P1", 500m, iban: StrangerIban));

        Assert.Equal(MatchingOptions.Default.MaxCandidates, set.Invoices.Count);
    }
}
