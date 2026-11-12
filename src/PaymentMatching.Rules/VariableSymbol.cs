using System.Text;

namespace PaymentMatching.Rules;

/// <summary>BR-08: keep ASCII digits only, strip leading zeros but keep at least one character.</summary>
public static class VariableSymbol
{
    public static string Normalize(string value)
    {
        var digits = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
        }

        var start = 0;
        while (digits.Length - start > 1 && digits[start] == '0')
        {
            start++;
        }

        return digits.ToString(start, digits.Length - start);
    }
}
