using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>
/// The payment identifies its invoice only up to a tie, so a person must decide:
/// (a) a VS with the last digit missing fits two consecutive invoices of one customer with the same
/// amount (a monthly fee), or (b) the VS names one invoice and two different combinations with it
/// fit the amount. Ground truth lists the invoices that were really paid.
/// </summary>
internal sealed class AmbiguousReferenceCase : ICaseBuilder
{
    public CaseType Type => CaseType.AmbiguousReference;

    public PaymentDraft Build(GenerationContext ctx) => ctx.Rng.Chance(0.5) ? TwoInvoicesOneTypo(ctx) : TwoCombinations(ctx);

    private PaymentDraft TwoInvoicesOneTypo(GenerationContext ctx)
    {
        var customer = ctx.RandomCustomer();
        var paid = ctx.IssueInvoice(customer);
        var fee = paid.Amount;

        // The twin must differ only in the last digit (…1234 and …1235, not …1239 and …1240).
        while (paid.VariableSymbol[^1] == '9')
        {
            paid = ctx.IssueInvoice(customer, fee);
        }

        var twin = ctx.IssueInvoice(customer, fee);
        var newest = twin.IssueDate > paid.IssueDate ? twin : paid;

        return new PaymentDraft(
            Type, ExpectedOutcome.HumanReview, ctx.BookingDateFor(newest), fee, "CZK",
            customer.Iban, ctx.PrintedName(customer), paid.VariableSymbol[..^1], "",
            [paid.InvoiceNumber], $"VS with the last digit missing fits {paid.InvoiceNumber} and {twin.InvoiceNumber}, same amount");
    }

    private PaymentDraft TwoCombinations(GenerationContext ctx)
    {
        var customer = ctx.RandomCustomer();
        var named = ctx.IssueInvoice(customer);
        var second = ctx.IssueInvoice(customer);
        var twin = ctx.IssueInvoice(customer, second.Amount);
        var newest = new[] { named, second, twin }.MaxBy(i => i.IssueDate)!;

        return new PaymentDraft(
            Type, ExpectedOutcome.HumanReview, ctx.BookingDateFor(newest), named.Amount + second.Amount, "CZK",
            customer.Iban, ctx.PrintedName(customer), named.VariableSymbol, $"úhrada faktur {named.InvoiceNumber} a další",
            [named.InvoiceNumber, second.InvoiceNumber],
            $"Fits {named.InvoiceNumber} + {second.InvoiceNumber} and {named.InvoiceNumber} + {twin.InvoiceNumber} (same amount)");
    }
}
