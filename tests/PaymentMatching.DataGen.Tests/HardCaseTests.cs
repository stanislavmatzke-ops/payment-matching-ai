using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Tests;

/// <summary>Hard case types of the eval profile. Checks that need the matching code live in PaymentMatching.Evals.Tests.</summary>
public class HardCaseTests
{
    private static readonly Dataset Eval = DataGenerator.Generate(new GeneratorOptions { Seed = 5005, CaseMix = GeneratorOptions.EvalCaseMix });

    [Fact]
    public void Eval_mix_sums_to_one_and_contains_every_case_type()
    {
        Assert.Equal(1.0, GeneratorOptions.EvalCaseMix.Values.Sum(), 9);
        Assert.Equal(Enum.GetValues<CaseType>().Length, Eval.Expected.Select(e => e.CaseType).Distinct().Count());
        Assert.Equal(30, Eval.Expected.Count(e => e.CaseType == CaseType.HiddenInjection));
    }

    [Fact]
    public void Default_mix_has_no_hard_cases()
    {
        var sample = DataGenerator.Generate(new GeneratorOptions());

        Assert.DoesNotContain(sample.Expected, e => e.CaseType is CaseType.AmbiguousReference or CaseType.UnparsedReference or CaseType.HiddenInjection);
    }

    [Fact]
    public void Ambiguous_payments_fit_a_second_invoice_or_combination_with_the_same_amount()
    {
        foreach (var (payment, expected, invoices) in Cases(CaseType.AmbiguousReference))
        {
            Assert.Equal(ExpectedOutcome.HumanReview, expected.Outcome);
            Assert.Equal(payment.Amount, invoices.Sum(i => i.Amount));
            var customer = invoices[0].CustomerId;
            var twins = Eval.Invoices.Where(i => i.CustomerId == customer && !expected.InvoiceNumbers.Contains(i.InvoiceNumber) && invoices.Any(p => p.Amount == i.Amount));
            Assert.NotEmpty(twins);
        }
    }

    [Fact]
    public void Unparsed_references_name_the_sequence_number_but_not_the_full_invoice_number()
    {
        foreach (var (payment, expected, invoices) in Cases(CaseType.UnparsedReference))
        {
            Assert.Equal(ExpectedOutcome.AutoMatch, expected.Outcome);
            var number = invoices.Single().InvoiceNumber;
            Assert.DoesNotContain(number, payment.Message, StringComparison.Ordinal);
            Assert.Contains(number.Split('-')[1], payment.Message, StringComparison.Ordinal);
            Assert.Equal("", payment.VariableSymbol);
            Assert.Equal(invoices[0].Amount, payment.Amount);
        }
    }

    [Fact]
    public void Hidden_injections_target_an_existing_invoice_and_never_match()
    {
        var numbers = Eval.Invoices.Select(i => i.InvoiceNumber).ToHashSet();
        foreach (var (payment, expected, invoices) in Cases(CaseType.HiddenInjection))
        {
            Assert.Equal(ExpectedOutcome.NoMatch, expected.Outcome);
            Assert.Empty(invoices);
            Assert.Contains(expected.InjectionTarget!, numbers);
            Assert.Contains(expected.InjectionTarget!, payment.Message + payment.CounterpartyName, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<(BankTransaction Payment, ExpectedMatch Expected, IReadOnlyList<Invoice> Invoices)> Cases(CaseType type)
    {
        var invoices = Eval.Invoices.ToDictionary(i => i.InvoiceNumber);
        var payments = Eval.Transactions.ToDictionary(t => t.PaymentId);
        var cases = Eval.Expected
            .Where(e => e.CaseType == type)
            .Select(e => (payments[e.PaymentId], e, (IReadOnlyList<Invoice>)e.InvoiceNumbers.Select(n => invoices[n]).ToList()))
            .ToList();

        Assert.NotEmpty(cases);
        return cases;
    }
}
