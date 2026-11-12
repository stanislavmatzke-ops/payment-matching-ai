using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>Correct VS and amount. Some payments are rounded to whole crowns (sub-crown difference).</summary>
internal sealed class ExactMatchCase : ICaseBuilder
{
    private static readonly string[] Messages = ["", "", "Platba faktury", "Úhrada", "Dekujeme"];

    public CaseType Type => CaseType.ExactMatch;

    public PaymentDraft Build(GenerationContext ctx)
    {
        var customer = ctx.RandomCustomer();
        var invoice = ctx.IssueInvoice(customer);

        var amount = invoice.Amount;
        var note = "VS and amount match";
        if (ctx.Rng.Chance(0.15) && decimal.Truncate(amount) != amount)
        {
            amount = decimal.Truncate(amount);
            note = "Paid rounded down to whole CZK (difference below 1 CZK)";
        }

        return new PaymentDraft(
            Type, ExpectedOutcome.AutoMatch, ctx.BookingDateFor(invoice), amount, "CZK",
            customer.Iban, ctx.PrintedName(customer), invoice.VariableSymbol, ctx.Rng.Pick(Messages),
            [invoice.InvoiceNumber], note)
        {
            ConstantSymbol = ctx.RandomConstantSymbol(),
        };
    }
}
