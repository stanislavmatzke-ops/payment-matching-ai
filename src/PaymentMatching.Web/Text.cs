using System.Collections.Concurrent;
using System.Globalization;

namespace PaymentMatching.Web;

/// <summary>
/// The app's texts in English (default) and Czech. The key is the English text itself, so a page reads as
/// English in code; <see cref="Czech"/> holds the translations. The language comes from the request culture
/// (cookie set by <c>/lang/{code}</c>), never from the URL of a page. A missing translation falls back to
/// English and is remembered in <see cref="Missing"/>, which a test renders every page to keep empty.
/// </summary>
public static partial class Text
{
    public const string English = "en";
    public const string Czech = "cs";
    public static readonly string[] Languages = [English, Czech];

    /// <summary>Keys asked for in Czech without a translation (for tests).</summary>
    public static ConcurrentDictionary<string, byte> Missing { get; } = new(StringComparer.Ordinal);

    public static bool IsCzech => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Czech;

    public static string Language => IsCzech ? Czech : English;

    public static string T(string english)
    {
        if (!IsCzech)
        {
            return english;
        }

        if (CzechTexts.TryGetValue(english, out var czech))
        {
            return czech;
        }

        Missing.TryAdd(english, 0);
        return english;
    }

    /// <summary>A translated format string with values (numbers formatted for the language).</summary>
    public static string T(string english, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, T(english), values);
}
