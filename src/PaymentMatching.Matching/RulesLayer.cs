using System.Text;
using PaymentMatching.Rules;

namespace PaymentMatching.Matching;

/// <summary>
/// Layer 1 of the strangler fig: the unchanged .NET port of the legacy rules.
/// The rules work on byte strings (Latin-1 view of the UTF-8 file), exactly as tools/Rules.Cli does,
/// so their output here is identical to legacy/golden/*/port_matches.csv.
/// </summary>
public static class RulesLayer
{
    public static IReadOnlyDictionary<string, MatchResult> Run(MatchingDataset data)
    {
        var invoices = data.Invoices
            .Select(i => new Invoice(Bytes(i.Number), i.VariableSymbol, Bytes(i.Kind), i.Amount))
            .ToList();
        var payments = data.Payments
            .Select(p => new Payment(Bytes(p.Id), p.Amount, Bytes(p.Currency), Bytes(p.CounterpartyName), Bytes(p.VariableSymbol), Bytes(p.Message)))
            .ToList();

        var results = PaymentMatcher.Match(invoices, payments);
        return data.Payments.Zip(results).ToDictionary(x => x.First.Id, x => x.Second);
    }

    /// <summary>The string the legacy code sees: one char per UTF-8 byte.</summary>
    private static string Bytes(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));
}
