using System.Globalization;
using System.Text;

namespace PaymentMatching.DataGen;

/// <summary>
/// Invented company and person names. Company roots are made-up words so that generated
/// names do not point at real businesses; any resemblance is accidental.
/// </summary>
internal static class FictionalNames
{
    private static readonly string[] CompanyRoots =
    [
        "Brezlon", "Kalmira", "Vostrex", "Zelvanta", "Doubrak", "Lunexa", "Hradiva", "Morvik",
        "Tesnar", "Klenovit", "Ostravel", "Pernika", "Rybolan", "Sázavit", "Trnovex", "Úvalka",
        "Večerka Nova", "Žulmar", "Čerpaton", "Řemenda", "Bělidlo", "Jasnovit", "Kovarna Brod", "Lipanex",
    ];

    private static readonly string[] Sectors =
    [
        "Stavby", "Logistika", "Obchod", "Strojírny", "Účetní služby", "Řemeslná dílna", "Elektro",
        "Potraviny", "Tiskárna", "Software", "Doprava", "Nábytek",
    ];

    private static readonly string[] LegalForms = ["s.r.o.", "s.r.o.", "s.r.o.", "a.s.", "v.o.s."];

    private static readonly string[] FirstNames = ["Jana", "Petr", "Eva", "Martin", "Lucie", "Tomáš", "Kateřina", "Jiří", "Hana", "Ondřej"];

    private static readonly (string Male, string Female)[] Surnames =
    [
        ("Zbořilek", "Zbořilková"), ("Hrdoň", "Hrdoňová"), ("Klenotek", "Klenotková"), ("Vrbínský", "Vrbínská"),
        ("Pšeničák", "Pšeničáková"), ("Mlýnecký", "Mlýnecká"), ("Daleček", "Dalečková"), ("Ostružník", "Ostružníková"),
    ];

    private static readonly HashSet<string> FemaleFirstNames = ["Jana", "Eva", "Lucie", "Kateřina", "Hana"];

    public static string Company(Random rng) =>
        $"{rng.Pick(CompanyRoots)} {rng.Pick(Sectors)} {rng.Pick(LegalForms)}";

    public static string Holding(Random rng) => $"{rng.Pick(CompanyRoots)} Holding a.s.";

    public static string Person(Random rng)
    {
        var first = rng.Pick(FirstNames);
        var (male, female) = rng.Pick(Surnames);
        return $"{first} {(FemaleFirstNames.Contains(first) ? female : male)}";
    }

    /// <summary>How banks often print a counterparty name: upper case, no diacritics, sometimes truncated.</summary>
    public static string AsBankWouldPrint(string name, Random rng)
    {
        var variant = rng.Next(4);
        return variant switch
        {
            0 => RemoveDiacritics(name).ToUpperInvariant(),
            1 => RemoveDiacritics(name),
            2 => name.Length > 20 ? name[..20] : name,
            _ => name,
        };
    }

    public static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
