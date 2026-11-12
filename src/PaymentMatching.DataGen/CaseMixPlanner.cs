using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen;

/// <summary>Turns case weights into exact counts (largest remainder method), so ratios hold even for small sets.</summary>
internal static class CaseMixPlanner
{
    public static List<CaseType> Plan(IReadOnlyDictionary<CaseType, double> weights, int total)
    {
        var shares = Enum.GetValues<CaseType>()
            // The epsilon keeps e.g. 0.06 * 300 = 17.999999… from flooring to 17.
            .Select(type => (Type: type, Exact: (weights.GetValueOrDefault(type) * total) + 1e-9))
            .Select(x => (x.Type, Count: (int)Math.Floor(x.Exact), Remainder: x.Exact - Math.Floor(x.Exact)))
            .ToList();

        var missing = total - shares.Sum(s => s.Count);
        var bonus = shares
            .OrderByDescending(s => s.Remainder)
            .ThenBy(s => s.Type)
            .Take(missing)
            .Select(s => s.Type)
            .ToHashSet();

        return shares
            .SelectMany(s => Enumerable.Repeat(s.Type, s.Count + (bonus.Contains(s.Type) ? 1 : 0)))
            .ToList();
    }
}
