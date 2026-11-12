using System.Globalization;
using System.Text;

namespace PaymentMatching.DataGen;

/// <summary>Builds and validates Czech IBANs (CZkk BBBB PPPPPP AAAAAAAAAA).</summary>
public static class CzechIban
{
    /// <summary>Bank code of the fictional "Demo Bank". Not assigned to any real Czech bank.</summary>
    public const string DemoBankCode = "9990";

    private static readonly int[] AccountWeights = [6, 3, 7, 9, 10, 5, 8, 4, 2, 1];

    /// <summary>Creates a random Demo Bank IBAN whose account number passes the Czech mod-11 check.</summary>
    public static string NewDemoBankIban(Random rng)
    {
        string account;
        do
        {
            account = rng.NextInt64(1_000_000, 10_000_000_000).ToString("D10", CultureInfo.InvariantCulture);
        }
        while (!IsValidAccountNumber(account));

        return Build(DemoBankCode, "000000", account);
    }

    public static string Build(string bankCode, string prefix, string account)
    {
        var bban = bankCode + prefix + account;
        var check = 98 - Mod97(bban + "CZ00");
        return "CZ" + check.ToString("D2", CultureInfo.InvariantCulture) + bban;
    }

    public static bool IsValid(string iban)
    {
        if (iban.Length != 24 || !iban.StartsWith("CZ", StringComparison.Ordinal) || !iban[2..].All(char.IsAsciiDigit))
        {
            return false;
        }

        return Mod97(iban[4..] + iban[..4]) == 1;
    }

    public static bool IsValidAccountNumber(string account)
    {
        var sum = 0;
        for (var i = 0; i < account.Length; i++)
        {
            sum += (account[i] - '0') * AccountWeights[i];
        }

        return sum % 11 == 0;
    }

    private static int Mod97(string value)
    {
        var digits = new StringBuilder();
        foreach (var c in value)
        {
            digits.Append(char.IsAsciiLetter(c) ? (char.ToUpperInvariant(c) - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString());
        }

        var remainder = 0;
        foreach (var d in digits.ToString())
        {
            remainder = ((remainder * 10) + (d - '0')) % 97;
        }

        return remainder;
    }
}
