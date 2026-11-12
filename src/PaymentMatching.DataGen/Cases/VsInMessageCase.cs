using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>No VS; the invoice number is written in the message in one of several human formats.</summary>
internal sealed class VsInMessageCase : ICaseBuilder
{
    public CaseType Type => CaseType.VsInMessage;

    public PaymentDraft Build(GenerationContext ctx)
    {
        var customer = ctx.RandomCustomer();
        var invoice = ctx.IssueInvoice(customer);
        var number = invoice.InvoiceNumber;
        var compact = number.Replace("-", "", StringComparison.Ordinal);
        var slashed = number.Replace('-', '/');

        string[] templates =
        [
            $"fa {number} díky",
            $"Faktura č. {number}",
            $"uhrada FA{compact}",
            $"platba za fakturu {slashed}",
            $"FV {number}",
        ];

        return new PaymentDraft(
            Type, ExpectedOutcome.AutoMatch, ctx.BookingDateFor(invoice), invoice.Amount, "CZK",
            customer.Iban, ctx.PrintedName(customer), "", ctx.Rng.Pick(templates),
            [invoice.InvoiceNumber], "Missing VS, invoice number in message");
    }
}
