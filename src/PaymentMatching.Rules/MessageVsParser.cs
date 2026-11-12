using System.Globalization;

namespace PaymentMatching.Rules;

/// <summary>
/// BR-10: reads an invoice number (YYYY-NNNN or YYYY/NNNN) after the first "FA" (or "FV")
/// in the payment message and turns it into a 10-digit VS. The message is untrusted input.
/// </summary>
public static class MessageVsParser
{
    private const int ScanWindow = 15;  // first digit must be within 15 chars (bytes) after the keyword
    private const int MinYear = 1990;
    private const int MaxSequenceDigits = 6;

    /// <returns>The VS, or an empty string when nothing usable is found.</returns>
    public static string Extract(string message)
    {
        var upper = PascalText.UpperAscii(message);

        var keyword = upper.IndexOf("FA", StringComparison.Ordinal);
        if (keyword < 0)
        {
            keyword = upper.IndexOf("FV", StringComparison.Ordinal);
        }

        if (keyword < 0)
        {
            return "";
        }

        var i = keyword + 2;
        var limit = i + ScanWindow;
        while (i < upper.Length && i < limit && !char.IsAsciiDigit(upper[i]))
        {
            i++;
        }

        if (i >= upper.Length || !char.IsAsciiDigit(upper[i]))
        {
            return "";
        }

        var year = upper.Substring(i, Math.Min(4, upper.Length - i));
        if (year.Length != 4
            || !int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out var yearNumber)
            || yearNumber < MinYear)
        {
            return "";
        }

        i += 4;
        if (i >= upper.Length || upper[i] is not ('-' or '/'))
        {
            return "";
        }

        i++;
        var sequenceStart = i;
        while (i < upper.Length && char.IsAsciiDigit(upper[i]) && i - sequenceStart < MaxSequenceDigits)
        {
            i++;
        }

        if (i == sequenceStart)
        {
            return "";
        }

        return year + upper[sequenceStart..i].PadLeft(MaxSequenceDigits, '0');
    }
}
