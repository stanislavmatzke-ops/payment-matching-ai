using System.Globalization;
using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>EUR payment for a CZK invoice with the correct VS. Ground truth stores the exchange rate used.</summary>
internal sealed class ForeignCurrencyCase : ICaseBuilder
{
    public CaseType Type => CaseType.ForeignCurrency;

    public PaymentDraft Build(GenerationContext ctx)
    {
        var customer = ctx.RandomCustomer();
        var invoice = ctx.IssueInvoice(customer);
        var rate = 24.80m + (ctx.Rng.Next(0, 80) / 100m);
        var amountEur = Math.Round(invoice.Amount / rate, 2, MidpointRounding.AwayFromZero);

        return new PaymentDraft(
            Type, ExpectedOutcome.AutoMatch, ctx.BookingDateFor(invoice), amountEur, "EUR",
            customer.Iban, ctx.PrintedName(customer), invoice.VariableSymbol, $"Invoice {invoice.InvoiceNumber}",
            [invoice.InvoiceNumber], string.Create(CultureInfo.InvariantCulture, $"EUR payment at {rate:0.00} CZK/EUR"))
        {
            FxRate = rate,
        };
    }
}
