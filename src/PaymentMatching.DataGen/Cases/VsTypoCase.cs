using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>VS with two swapped digits or one missing digit; amount and payer are correct.</summary>
internal sealed class VsTypoCase : ICaseBuilder
{
    public CaseType Type => CaseType.VsTypo;

    public PaymentDraft Build(GenerationContext ctx)
    {
        var customer = ctx.RandomCustomer();
        var invoice = ctx.IssueInvoice(customer);
        var (typo, note) = ctx.Rng.Chance(0.6)
            ? (SwapAdjacentDigits(invoice.VariableSymbol, ctx.Rng), "Two adjacent VS digits swapped")
            : (DropDigit(invoice.VariableSymbol, ctx.Rng), "One VS digit missing");

        return new PaymentDraft(
            Type, ExpectedOutcome.AutoMatch, ctx.BookingDateFor(invoice), invoice.Amount, "CZK",
            customer.Iban, ctx.PrintedName(customer), typo, "",
            [invoice.InvoiceNumber], note);
    }

    /// <summary>Swaps two different adjacent digits in the sequence part (last 4 digits).</summary>
    public static string SwapAdjacentDigits(string vs, Random rng)
    {
        var candidates = Enumerable.Range(vs.Length - 4, 3).Where(i => vs[i] != vs[i + 1]).ToList();
        if (candidates.Count == 0)
        {
            return DropDigit(vs, rng);
        }

        var i = rng.Pick(candidates);
        var chars = vs.ToCharArray();
        (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
        return new string(chars);
    }

    /// <summary>Removes one digit. The result has 9 digits, so it can never equal another invoice VS.</summary>
    public static string DropDigit(string vs, Random rng) => vs.Remove(rng.Next(vs.Length - 4, vs.Length), 1);
}
