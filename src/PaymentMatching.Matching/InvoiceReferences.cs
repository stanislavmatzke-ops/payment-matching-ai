using System.Text.RegularExpressions;

namespace PaymentMatching.Matching;

/// <summary>Finds invoice numbers and variable symbols in free text, and near-miss variable symbols.</summary>
public static partial class InvoiceReferences
{
    /// <summary>
    /// "2026-1234", "2026/1234", "20261234" and the 10-digit VS "2026001234" all give "2026-1234".
    /// Year 1990–2099, sequence 1–6 digits.
    /// </summary>
    public static IReadOnlySet<string> InMessage(string message)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in InvoiceNumberPattern().Matches(message))
        {
            var sequence = m.Groups["seq"].Value.TrimStart('0');
            if (sequence.Length > 0)
            {
                found.Add($"{m.Groups["year"].Value}-{sequence}");
            }
        }

        return found;
    }

    /// <summary>Canonical form of an invoice number for comparison with <see cref="InMessage"/>.</summary>
    public static string Canonical(string invoiceNumber)
    {
        var refs = InMessage(invoiceNumber);
        return refs.Count == 1 ? refs.First() : invoiceNumber;
    }

    /// <summary>
    /// True when two digit strings differ by one typing error: two adjacent digits swapped, one digit
    /// missing, one extra or one wrong (Damerau-Levenshtein distance 1).
    /// </summary>
    public static bool IsOneTypoApart(string a, string b)
    {
        if (a == b || a.Length == 0 || b.Length == 0 || Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        if (a.Length == b.Length)
        {
            var diffs = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToList();
            return diffs.Count == 1
                || (diffs.Count == 2 && diffs[1] == diffs[0] + 1 && a[diffs[0]] == b[diffs[1]] && a[diffs[1]] == b[diffs[0]]);
        }

        var (longer, shorter) = a.Length > b.Length ? (a, b) : (b, a);
        return Enumerable.Range(0, longer.Length).Any(i => longer.Remove(i, 1) == shorter);
    }

    [GeneratedRegex(@"(?<!\d)(?<year>(19|20)\d{2})\s?[-/]?\s?(?<seq>\d{1,6})(?!\d)")]
    private static partial Regex InvoiceNumberPattern();
}
