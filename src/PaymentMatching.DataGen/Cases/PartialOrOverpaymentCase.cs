using System.Globalization;
using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>Correct VS, but the amount is a partial payment or an overpayment. A human must decide.</summary>
internal sealed class PartialOrOverpaymentCase : ICaseBuilder
{
    public CaseType Type => CaseType.PartialOrOverpayment;

    public PaymentDraft Build(GenerationContext ctx)
    {
        var customer = ctx.RandomCustomer();
        var invoice = ctx.IssueInvoice(customer);

        var partial = ctx.Rng.Chance(0.6);
        decimal amount;
        if (partial)
        {
            var fraction = ctx.Rng.Next(30, 91) / 100m;
            amount = Math.Max(100m, decimal.Floor(invoice.Amount * fraction / 100m) * 100m);
        }
        else
        {
            amount = invoice.Amount + (ctx.Rng.Next(1, 50) * 10m);
        }

        var note = string.Create(
            CultureInfo.InvariantCulture,
            $"{(partial ? "Partial payment" : "Overpayment")}: {amount:0.00} of {invoice.Amount:0.00} CZK");

        return new PaymentDraft(
            Type, ExpectedOutcome.HumanReview, ctx.BookingDateFor(invoice), amount, "CZK",
            customer.Iban, ctx.PrintedName(customer), invoice.VariableSymbol, partial ? "splatka" : "",
            [invoice.InvoiceNumber], note);
    }
}
