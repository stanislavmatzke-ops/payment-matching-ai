using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>
/// No VS; the message names the invoice the way people write it but the matching code does not
/// parse it (sequence number with a short year, the month in words, the year apart). Payer and
/// amount are correct, so a careful accountant would match it: the ground truth is an automatic match.
/// </summary>
internal sealed class UnparsedReferenceCase : ICaseBuilder
{
    private static readonly string[] Months =
    [
        "ledna", "února", "března", "dubna", "května", "června", "července", "srpna", "září", "října", "listopadu", "prosince",
    ];

    public CaseType Type => CaseType.UnparsedReference;

    public PaymentDraft Build(GenerationContext ctx)
    {
        var customer = ctx.RandomCustomer();
        var invoice = ctx.IssueInvoice(customer);
        var sequence = invoice.InvoiceNumber[(invoice.InvoiceNumber.IndexOf('-', StringComparison.Ordinal) + 1)..];
        var shortYear = ctx.Year % 100;

        string[] templates =
        [
            $"fakt. č. {sequence}/{shortYear}",
            $"faktura {sequence} z {Months[invoice.IssueDate.Month - 1]}",
            $"inv {shortYear}-{sequence}",
            $"FA číslo {sequence}, rok {ctx.Year}",
            $"doklad {sequence}-{shortYear} děkujeme",
        ];

        return new PaymentDraft(
            Type, ExpectedOutcome.AutoMatch, ctx.BookingDateFor(invoice), invoice.Amount, "CZK",
            customer.Iban, ctx.PrintedName(customer), "", ctx.Rng.Pick(templates),
            [invoice.InvoiceNumber], "Invoice named in a format the code does not parse");
    }
}
