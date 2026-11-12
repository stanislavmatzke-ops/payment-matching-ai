using System.Globalization;
using System.Text;

namespace PaymentMatching.Rules;

/// <summary>
/// Reads the generator CSV files and writes the result file exactly like the legacy CLI
/// (BR-01..BR-04, BR-20..BR-22, docs/business-rules.md section 6).
/// </summary>
/// <remarks>
/// Files are decoded as Latin-1, so every byte becomes one char. The legacy code works on byte
/// strings; this keeps lengths, the 15-byte message window and ASCII-only upper-casing identical,
/// and writing Latin-1 back reproduces any UTF-8 input byte for byte.
/// </remarks>
public static class LegacyCsv
{
    public const string InvoicesFile = "invoices.csv";
    public const string StatementFile = "bank_statement.csv";
    public const string ResultHeader = "payment_id;status;invoice_numbers;rule;amount_diff";

    public static IReadOnlyList<Invoice> ReadInvoices(string path) =>
        ReadRecords(path, minFields: 8)
            .Select(f => new Invoice(f[0], VariableSymbol.Normalize(f[1]), f[3], ParseAmount(f[6])))
            .ToList();

    public static IReadOnlyList<Payment> ReadPayments(string path) =>
        ReadRecords(path, minFields: 10)
            .Select(f => new Payment(f[0], ParseAmount(f[2]), f[3], f[5], f[6], f[9]))
            .ToList();

    public static string WriteResults(IEnumerable<MatchResult> results)
    {
        var sb = new StringBuilder(ResultHeader).Append('\n');
        foreach (var r in results)
        {
            var diff = r.AmountDiff is { } d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "";
            sb.Append(r.PaymentId).Append(';')
              .Append(StatusCode(r.Status)).Append(';')
              .Append(r.InvoiceNumber).Append(';')
              .Append(Quote(r.Rule)).Append(';')
              .Append(diff).Append('\n');
        }

        return sb.ToString();
    }

    public static string ReadText(string path) => File.ReadAllText(path, Encoding.Latin1);

    public static void WriteText(string path, string content) => File.WriteAllText(path, content, Encoding.Latin1);

    public static char StatusCode(MatchStatus status) => status switch
    {
        MatchStatus.Paired => 'P',
        MatchStatus.Partial => 'C',
        MatchStatus.None => 'N',
        MatchStatus.Skipped => 'X',
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>BR-03: one physical line, ';' separator, quotes may start anywhere, "" inside quotes.</summary>
    public static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c != '"')
                {
                    current.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ';')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>BR-04: invariant decimal point; anything unparseable silently becomes 0.</summary>
    public static decimal ParseAmount(string text) =>
        decimal.TryParse(PascalText.Trim(text), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>BR-01/02: skip the header line, blank lines and lines with too few fields.</summary>
    private static IEnumerable<List<string>> ReadRecords(string path, int minFields) =>
        ReadText(path)
            .Split('\n')
            .Skip(1)
            .Where(line => PascalText.Trim(line).Length > 0)
            .Select(SplitLine)
            .Where(fields => fields.Count >= minFields);

    private static string Quote(string field) =>
        field.Contains(';') || field.Contains('"') ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
}
