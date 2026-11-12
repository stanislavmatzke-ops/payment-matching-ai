using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>One payment settles 2–4 invoices of the same customer.</summary>
internal sealed class MultiInvoiceCase : ICaseBuilder
{
    public CaseType Type => CaseType.MultiInvoice;

    public PaymentDraft Build(GenerationContext ctx)
    {
        var customer = ctx.RandomCustomer();
        var invoices = Enumerable.Range(0, ctx.Rng.Next(2, 5)).Select(_ => ctx.IssueInvoice(customer)).ToList();
        var numbers = invoices.Select(i => i.InvoiceNumber).ToList();
        var newest = invoices.MaxBy(i => i.IssueDate)!;

        var listInMessage = ctx.Rng.Chance(0.6);
        var vs = listInMessage ? "" : invoices[0].VariableSymbol;
        var message = listInMessage
            ? "fa " + string.Join(", ", numbers)
            : $"úhrada faktur {numbers[0]} a další";

        return new PaymentDraft(
            Type, ExpectedOutcome.AutoMatch, ctx.BookingDateFor(newest), invoices.Sum(i => i.Amount), "CZK",
            customer.Iban, ctx.PrintedName(customer), vs, message,
            numbers, $"One payment for {invoices.Count} invoices");
    }
}
