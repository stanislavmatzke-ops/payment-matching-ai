namespace PaymentMatching.DataGen;

internal static class RandomExtensions
{
    public static T Pick<T>(this Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    public static bool Chance(this Random rng, double probability) => rng.NextDouble() < probability;

    /// <summary>Fisher–Yates shuffle in place.</summary>
    public static void Shuffle<T>(this Random rng, IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
