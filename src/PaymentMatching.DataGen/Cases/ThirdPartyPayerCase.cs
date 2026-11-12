using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>A parent company or a factoring company pays the customer invoice. Needs human confirmation.</summary>
internal sealed class ThirdPartyPayerCase : ICaseBuilder
{
    public const string FactoringCompany = "Demo Faktoring a.s.";

    public CaseType Type => CaseType.ThirdPartyPayer;

    public PaymentDraft Build(GenerationContext ctx)
    {
        var customer = ctx.RandomCustomer();
        var invoice = ctx.IssueInvoice(customer);
        var factoring = ctx.Rng.Chance(0.5);

        var payerName = factoring ? FactoringCompany : FictionalNames.Holding(ctx.Rng);
        var message = factoring
            ? $"postoupena pohledavka fa {invoice.InvoiceNumber}"
            : $"za {customer.Name}, fa {invoice.InvoiceNumber}";

        return new PaymentDraft(
            Type, ExpectedOutcome.HumanReview, ctx.BookingDateFor(invoice), invoice.Amount, "CZK",
            CzechIban.NewDemoBankIban(ctx.Rng), payerName, "", message,
            [invoice.InvoiceNumber], factoring ? "Paid by factoring company" : "Paid by parent company");
    }
}
