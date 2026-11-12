using PaymentMatching.DataGen.Csv;
using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Tests;

public class DataGeneratorTests
{
    private static readonly Dataset Sample = DataGenerator.Generate(new GeneratorOptions { Seed = 42, PaymentCount = 300 });

    [Fact]
    public void Same_seed_produces_identical_csv()
    {
        var first = DatasetCsv.ToCsv(DataGenerator.Generate(new GeneratorOptions { Seed = 7 }));
        var second = DatasetCsv.ToCsv(DataGenerator.Generate(new GeneratorOptions { Seed = 7 }));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_seed_produces_different_data()
    {
        var first = DatasetCsv.ToCsv(DataGenerator.Generate(new GeneratorOptions { Seed = 1 }));
        var second = DatasetCsv.ToCsv(DataGenerator.Generate(new GeneratorOptions { Seed = 2 }));

        Assert.NotEqual(first[DatasetCsv.StatementFile], second[DatasetCsv.StatementFile]);
    }

    [Fact]
    public void Case_mix_matches_configured_ratios_exactly()
    {
        var counts = Sample.Expected.GroupBy(e => e.CaseType).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(300, Sample.Transactions.Count);
        foreach (var (type, weight) in GeneratorOptions.DefaultCaseMix)
        {
            Assert.Equal((int)Math.Round(weight * 300), counts[type]);
        }
    }

    [Fact]
    public void Small_dataset_still_contains_every_case_type()
    {
        var small = DataGenerator.Generate(new GeneratorOptions { PaymentCount = 20 });

        Assert.Equal(20, small.Transactions.Count);
        Assert.Equal(GeneratorOptions.DefaultCaseMix.Count, small.Expected.Select(e => e.CaseType).Distinct().Count());
    }

    [Fact]
    public void Every_payment_has_exactly_one_ground_truth_row()
    {
        Assert.Equal(Sample.Transactions.Select(t => t.PaymentId), Sample.Expected.Select(e => e.PaymentId));
        Assert.Equal(Sample.Transactions.Count, Sample.Transactions.Select(t => t.PaymentId).Distinct().Count());
    }

    [Fact]
    public void Expected_invoices_exist_and_belong_to_one_customer()
    {
        var invoices = Sample.Invoices.ToDictionary(i => i.InvoiceNumber);

        foreach (var expected in Sample.Expected.Where(e => e.Outcome != ExpectedOutcome.NoMatch))
        {
            Assert.NotEmpty(expected.InvoiceNumbers);
            Assert.All(expected.InvoiceNumbers, n => Assert.True(invoices.ContainsKey(n), n));
            Assert.Single(expected.InvoiceNumbers.Select(n => invoices[n].CustomerId).Distinct());
        }
    }

    [Fact]
    public void Exact_matches_have_correct_vs_and_amount_within_one_crown()
    {
        foreach (var (payment, expected, invoices) in Cases(CaseType.ExactMatch))
        {
            var invoice = Assert.Single(invoices);
            Assert.Equal(invoice.VariableSymbol, payment.VariableSymbol);
            Assert.InRange(invoice.Amount - payment.Amount, 0m, 0.99m);
            Assert.Equal(ExpectedOutcome.AutoMatch, expected.Outcome);
        }
    }

    [Fact]
    public void Some_exact_matches_have_a_sub_crown_difference()
    {
        Assert.Contains(Cases(CaseType.ExactMatch), c => c.Payment.Amount != c.Invoices[0].Amount);
    }

    [Fact]
    public void Vs_in_message_cases_have_no_vs_and_mention_the_invoice()
    {
        foreach (var (payment, _, invoices) in Cases(CaseType.VsInMessage))
        {
            var invoice = Assert.Single(invoices);
            Assert.Empty(payment.VariableSymbol);
            Assert.Equal(invoice.Amount, payment.Amount);
            var digits = new string(payment.Message.Where(char.IsAsciiDigit).ToArray());
            Assert.Contains(invoice.InvoiceNumber.Replace("-", "", StringComparison.Ordinal), digits, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Vs_typos_never_equal_any_invoice_vs()
    {
        var allVs = Sample.Invoices.Select(i => i.VariableSymbol).ToHashSet();

        foreach (var (payment, _, invoices) in Cases(CaseType.VsTypo))
        {
            Assert.NotEqual(invoices[0].VariableSymbol, payment.VariableSymbol);
            Assert.DoesNotContain(payment.VariableSymbol, allVs);
            Assert.Equal(invoices[0].Amount, payment.Amount);
        }
    }

    [Fact]
    public void Multi_invoice_payment_equals_sum_of_invoices()
    {
        foreach (var (payment, _, invoices) in Cases(CaseType.MultiInvoice))
        {
            Assert.InRange(invoices.Count, 2, 4);
            Assert.Equal(invoices.Sum(i => i.Amount), payment.Amount);
        }
    }

    [Fact]
    public void Partial_and_overpayments_go_to_human_review()
    {
        foreach (var (payment, expected, invoices) in Cases(CaseType.PartialOrOverpayment))
        {
            Assert.Equal(ExpectedOutcome.HumanReview, expected.Outcome);
            Assert.NotEqual(invoices[0].Amount, payment.Amount);
            Assert.Equal(invoices[0].VariableSymbol, payment.VariableSymbol);
        }
    }

    [Fact]
    public void Third_party_payer_is_not_the_customer()
    {
        var customers = Sample.Customers.ToDictionary(c => c.CustomerId);

        foreach (var (payment, expected, invoices) in Cases(CaseType.ThirdPartyPayer))
        {
            var customer = customers[invoices[0].CustomerId];
            Assert.NotEqual(customer.Iban, payment.CounterpartyIban);
            Assert.NotEqual(customer.Name, payment.CounterpartyName);
            Assert.Equal(ExpectedOutcome.HumanReview, expected.Outcome);
        }
    }

    [Fact]
    public void Foreign_currency_amount_converts_back_with_stored_rate()
    {
        foreach (var (payment, expected, invoices) in Cases(CaseType.ForeignCurrency))
        {
            Assert.Equal("EUR", payment.Currency);
            var rate = Assert.NotNull(expected.FxRate);
            Assert.InRange(Math.Abs((payment.Amount * rate) - invoices[0].Amount), 0m, rate / 100m);
        }
    }

    [Fact]
    public void Unrelated_payments_must_not_match()
    {
        var allVs = Sample.Invoices.Select(i => i.VariableSymbol).ToHashSet();

        foreach (var (payment, expected, invoices) in Cases(CaseType.Unrelated))
        {
            Assert.Equal(ExpectedOutcome.NoMatch, expected.Outcome);
            Assert.Empty(invoices);
            Assert.DoesNotContain(payment.VariableSymbol, allVs);
        }
    }

    [Fact]
    public void Prompt_injections_must_not_match_and_name_their_target()
    {
        var injections = Cases(CaseType.PromptInjection).ToList();
        var invoiceNumbers = Sample.Invoices.Select(i => i.InvoiceNumber).ToHashSet();

        Assert.All(injections, c =>
        {
            Assert.Equal(ExpectedOutcome.NoMatch, c.Expected.Outcome);
            Assert.Empty(c.Invoices);
            var target = Assert.IsType<string>(c.Expected.InjectionTarget);
            Assert.Contains(target, c.Payment.Message, StringComparison.Ordinal);
        });
        Assert.Contains(injections, c => invoiceNumbers.Contains(c.Expected.InjectionTarget!));
        Assert.Contains(injections, c => !invoiceNumbers.Contains(c.Expected.InjectionTarget!));
    }

    [Fact]
    public void All_ibans_are_valid_demo_bank_accounts()
    {
        var ibans = Sample.Customers.Select(c => c.Iban).Concat(Sample.Transactions.Select(t => t.CounterpartyIban));

        Assert.All(ibans, iban =>
        {
            Assert.True(CzechIban.IsValid(iban), iban);
            Assert.Equal(CzechIban.DemoBankCode, iban[4..8]);
        });
    }

    [Fact]
    public void Dates_never_exceed_reference_date_and_payments_follow_their_invoices()
    {
        var reference = new GeneratorOptions().ReferenceDate;

        Assert.All(Sample.Transactions, t => Assert.True(t.BookingDate <= reference));
        foreach (var type in new[] { CaseType.ExactMatch, CaseType.VsInMessage, CaseType.MultiInvoice })
        {
            Assert.All(Cases(type), c => Assert.All(c.Invoices, i => Assert.True(c.Payment.BookingDate > i.IssueDate)));
        }
    }

    [Fact]
    public void Dataset_contains_advance_invoices()
    {
        Assert.Contains(Sample.Invoices, i => i.Kind == InvoiceKind.Advance);
    }

    [Fact]
    public void Invalid_case_mix_is_rejected()
    {
        var options = new GeneratorOptions { CaseMix = new Dictionary<CaseType, double> { [CaseType.ExactMatch] = 0.5 } };

        Assert.Throws<ArgumentException>(() => DataGenerator.Generate(options));
    }

    private static IEnumerable<(BankTransaction Payment, ExpectedMatch Expected, IReadOnlyList<Invoice> Invoices)> Cases(CaseType type)
    {
        var invoices = Sample.Invoices.ToDictionary(i => i.InvoiceNumber);
        var payments = Sample.Transactions.ToDictionary(t => t.PaymentId);

        var cases = Sample.Expected
            .Where(e => e.CaseType == type)
            .Select(e => (payments[e.PaymentId], e, (IReadOnlyList<Invoice>)e.InvoiceNumbers.Select(n => invoices[n]).ToList()))
            .ToList();

        Assert.NotEmpty(cases);
        return cases;
    }
}
