using System.Globalization;
using PaymentMatching.DataGen.Cases;
using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen;

/// <summary>
/// Generates a synthetic bank statement with invoices and ground truth. The same options
/// (including the seed) always produce exactly the same dataset.
/// </summary>
public static class DataGenerator
{
    private static readonly IReadOnlyDictionary<CaseType, ICaseBuilder> Builders = new ICaseBuilder[]
    {
        new ExactMatchCase(),
        new VsInMessageCase(),
        new VsTypoCase(),
        new MultiInvoiceCase(),
        new PartialOrOverpaymentCase(),
        new ThirdPartyPayerCase(),
        new ForeignCurrencyCase(),
        new UnrelatedCase(),
        new PromptInjectionCase(),
        new AmbiguousReferenceCase(),
        new UnparsedReferenceCase(),
        new HiddenInjectionCase(),
    }.ToDictionary(b => b.Type);

    public static Dataset Generate(GeneratorOptions options)
    {
        options.Validate();
        var rng = new Random(options.Seed);
        var ctx = new GenerationContext(rng, options);

        var customerCount = Math.Max(10, options.PaymentCount / 5);
        for (var i = 0; i < customerCount; i++)
        {
            ctx.AddCustomer();
        }

        // Open (unpaid) invoices first, so they can serve as distractors and injection targets.
        var openInvoices = (int)Math.Round(options.PaymentCount * options.OpenInvoiceRatio, MidpointRounding.AwayFromZero);
        for (var i = 0; i < openInvoices; i++)
        {
            ctx.IssueInvoice(ctx.RandomCustomer());
        }

        var plan = CaseMixPlanner.Plan(options.CaseMix, options.PaymentCount);
        rng.Shuffle(plan);

        var drafts = plan.Select(type => Builders[type].Build(ctx)).ToList();
        drafts = FixTypoCollisions(drafts, ctx);

        return Assemble(ctx, drafts);
    }

    /// <summary>A swapped-digit VS may accidentally equal another invoice VS. Such typos become a dropped digit.</summary>
    private static List<PaymentDraft> FixTypoCollisions(List<PaymentDraft> drafts, GenerationContext ctx)
    {
        var invoicesByNumber = ctx.Invoices.ToDictionary(i => i.InvoiceNumber);
        var allVs = ctx.Invoices.Select(i => i.VariableSymbol).ToHashSet();

        return drafts.Select(d =>
        {
            if (d.CaseType != CaseType.VsTypo || !allVs.Contains(d.VariableSymbol))
            {
                return d;
            }

            var original = invoicesByNumber[d.InvoiceNumbers[0]].VariableSymbol;
            return d with { VariableSymbol = VsTypoCase.DropDigit(original, ctx.Rng), Note = "One VS digit missing" };
        }).ToList();
    }

    private static Dataset Assemble(GenerationContext ctx, List<PaymentDraft> drafts)
    {
        var ordered = drafts
            .Select((draft, index) => (draft, index))
            .OrderBy(x => x.draft.BookingDate)
            .ThenBy(x => x.index)
            .Select((x, i) => (Draft: x.draft, Id: "P" + (i + 1).ToString("D4", CultureInfo.InvariantCulture)))
            .ToList();

        var transactions = ordered.Select(x => new BankTransaction(
            x.Id, x.Draft.BookingDate, x.Draft.Amount, x.Draft.Currency, x.Draft.CounterpartyIban,
            x.Draft.CounterpartyName, x.Draft.VariableSymbol, x.Draft.ConstantSymbol, x.Draft.SpecificSymbol,
            x.Draft.Message)).ToList();

        var expected = ordered.Select(x => new ExpectedMatch(
            x.Id, x.Draft.CaseType, x.Draft.Outcome, x.Draft.InvoiceNumbers, x.Draft.FxRate,
            x.Draft.InjectionTarget, x.Draft.Note)).ToList();

        return new Dataset(ctx.Customers.ToList(), ctx.Invoices.ToList(), transactions, expected);
    }
}
