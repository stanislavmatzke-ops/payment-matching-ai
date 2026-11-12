using System.Globalization;
using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>Incoming money that belongs to no invoice: refunds, interest, insurance, gifts.</summary>
internal sealed class UnrelatedCase : ICaseBuilder
{
    private static readonly string[] Messages =
    [
        "Vrácení přeplatku", "Úrok za období", "Vratka zálohy za energie", "Pojistné plnění", "Dar", "Vrácení poplatku",
    ];

    public CaseType Type => CaseType.Unrelated;

    public PaymentDraft Build(GenerationContext ctx)
    {
        var fromBank = ctx.Rng.Chance(0.3);
        var name = fromBank ? "Demo Bank" : FictionalNames.Person(ctx.Rng);
        var amount = ctx.Rng.Next(50, 20_000) + (ctx.Rng.Next(0, 100) / 100m);

        // A VS starting with 9 can never collide with an invoice VS (those start with the year).
        var vs = ctx.Rng.Chance(0.5)
            ? "9" + ctx.Rng.Next(100_000_000, 1_000_000_000).ToString(CultureInfo.InvariantCulture)
            : "";

        return new PaymentDraft(
            Type, ExpectedOutcome.NoMatch, ctx.RandomBookingDate(), amount, "CZK",
            CzechIban.NewDemoBankIban(ctx.Rng), name, vs, ctx.Rng.Pick(Messages),
            [], "Not related to any invoice");
    }
}
