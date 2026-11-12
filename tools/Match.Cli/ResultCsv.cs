using System.Globalization;
using System.Text;
using PaymentMatching.Matching;

namespace Match.Cli;

/// <summary>matches.csv: one line per payment with the decision and who made it.</summary>
internal static class ResultCsv
{
    public static string Write(IEnumerable<AuditRecord> records)
    {
        var sb = new StringBuilder("payment_id;outcome;invoice_numbers;decided_by;confidence;flags;reason\n");
        foreach (var r in records)
        {
            var d = r.Decision;
            sb.AppendJoin(';',
            [
                d.PaymentId,
                d.Outcome.ToString(),
                string.Join('|', d.InvoiceNumbers),
                d.DecidedBy,
                d.Confidence?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
                string.Join('|', d.Flags),
                Quote(d.Reason),
            ]).Append('\n');
        }

        return sb.ToString();
    }

    private static string Quote(string field) =>
        field.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
}
