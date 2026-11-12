using System.Text;

namespace PaymentMatching.DataGen.Csv;

/// <summary>Minimal RFC 4180 writer with ';' as separator and '\n' line endings (stable across platforms).</summary>
public static class CsvWriter
{
    public const char Separator = ';';

    public static string Write(IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, header);
        foreach (var row in rows)
        {
            AppendRow(sb, row);
        }

        return sb.ToString();
    }

    public static string Escape(string field)
    {
        var needsQuotes = field.Contains(Separator) || field.Contains('"') || field.Contains('\n') || field.Contains('\r');
        return needsQuotes ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
    }

    private static void AppendRow(StringBuilder sb, IEnumerable<string> fields)
    {
        sb.AppendJoin(Separator, fields.Select(Escape));
        sb.Append('\n');
    }
}
