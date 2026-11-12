using System.Globalization;
using System.Text;

namespace PaymentMatching.Matching;

/// <summary>
/// Cheap heuristic for text that addresses an AI or gives orders inside bank data. It is one layer
/// of defence: it only adds a flag. Validation by code is what actually prevents a wrong match.
/// </summary>
public static class InjectionDetector
{
    private static readonly string[] Markers =
    [
        "ignore previous", "ignore all", "ignore the above", "previous instructions", "new instruction",
        "system:", "assistant:", "</", "<|", "confidence 1", "skip human", "human review", "pre-approved",
        "ignoruj", "pokyn", "pro ai", "ai asistent", "kontrola neni potreba", "jako zaplacen",
    ];

    public static bool LooksLikeInjection(string text)
    {
        var folded = Fold(text);
        return Markers.Any(m => folded.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>Lower case without diacritics, so "Ignoruj předchozí" and "IGNORUJ PREDCHOZI" are the same.</summary>
    private static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }
}
