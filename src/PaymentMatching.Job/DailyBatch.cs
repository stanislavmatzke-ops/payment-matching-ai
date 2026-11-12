using System.Globalization;
using PaymentMatching.DataGen;
using PaymentMatching.DataGen.Csv;
using PaymentMatching.DataGen.Model;
using PaymentMatching.Matching;

namespace PaymentMatching.Job;

/// <summary>
/// The shared daily batch (ADR 0013): 3–5 new payments with their invoices and customers, the same for
/// every sandbox on a given day (the seed is the date). Most are cases the rules settle; 1–2 are cases a
/// model is worth asking about. Ids carry the date (customers <c>C261112-001</c>, payments
/// <c>P261112-0001</c>, invoices <c>2026-13xxxx</c>), so they never collide with the starter data or
/// another day's batch, and the prompt for a batch payment is the same in every sandbox: one answer,
/// cached, serves all of them.
/// </summary>
public sealed record DailyBatch(DateOnly Day, Dataset Generated, MatchingDataset Data)
{
    /// <summary>
    /// Always settled by the rules layer (or sent to a person by its policy) without a model. Measured over
    /// 400 days: partial payments and a VS in the message sometimes reach the model, so they are not here.
    /// </summary>
    internal static readonly CaseType[] RuleCases =
    [
        CaseType.ExactMatch, CaseType.ExactMatch, CaseType.ExactMatch, CaseType.ThirdPartyPayer,
    ];

    /// <summary>Always reach the AI layer: an unreadable invoice number, a VS typo, several invoices at once, now and then an injection attempt.</summary>
    internal static readonly CaseType[] AiCases =
    [
        CaseType.UnparsedReference, CaseType.UnparsedReference, CaseType.VsTypo, CaseType.MultiInvoice,
        CaseType.HiddenInjection, CaseType.PromptInjection,
    ];

    /// <summary>Invoice numbers of a batch start here plus 100 per day of the year (starter data uses 1001+).</summary>
    private const int FirstSequence = 100_000;

    public static DailyBatch For(DateOnly day)
    {
        var seed = (day.Year * 10_000) + (day.Month * 100) + day.Day;
        var rng = new Random(seed);
        var total = rng.Next(3, 6);
        var ai = rng.Next(1, 3);
        var plan = Enumerable.Range(0, total - ai).Select(_ => RuleCases[rng.Next(RuleCases.Length)])
            .Concat(Enumerable.Range(0, ai).Select(_ => AiCases[rng.Next(AiCases.Length)]))
            .ToList();

        // Exact counts per type as weights; the planner's largest remainder method gives them back exactly.
        var mix = plan.GroupBy(t => t).ToDictionary(g => g.Key, g => (double)g.Count() / total);
        var prefix = day.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var generated = DataGenerator.Generate(new GeneratorOptions
        {
            Seed = seed,
            PaymentCount = total,
            ReferenceDate = day,
            CaseMix = mix,
            MinCustomers = 3,
            CustomerIdPrefix = $"C{prefix}-",
            PaymentIdPrefix = $"P{prefix}-",
            FirstInvoiceSequence = FirstSequence + (day.DayOfYear * 100),
        });

        var csv = DatasetCsv.ToCsv(generated);
        var data = MatchingDataset.Parse(csv[DatasetCsv.CustomersFile], csv[DatasetCsv.InvoicesFile], csv[DatasetCsv.StatementFile]);
        return new DailyBatch(day, generated, data);
    }
}
