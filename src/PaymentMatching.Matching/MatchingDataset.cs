using System.Globalization;
using System.Text;
using PaymentMatching.Rules;

namespace PaymentMatching.Matching;

/// <summary>Customers, invoices and one bank statement, read from the generator CSV files (docs/data-format.md).</summary>
public sealed class MatchingDataset
{
    public MatchingDataset(IReadOnlyList<Customer> customers, IReadOnlyList<InvoiceRecord> invoices, IReadOnlyList<BankPayment> payments)
    {
        Customers = customers;
        Invoices = invoices;
        Payments = payments;
        CustomerById = customers.ToDictionary(c => c.Id);
        InvoiceByNumber = invoices.GroupBy(i => i.Number).ToDictionary(g => g.Key, g => g.First());
        CustomerByIban = customers
            .GroupBy(c => NormalizeIban(c.Iban))
            .ToDictionary(g => g.Key, g => g.First());
    }

    public IReadOnlyList<Customer> Customers { get; }

    public IReadOnlyList<InvoiceRecord> Invoices { get; }

    public IReadOnlyList<BankPayment> Payments { get; }

    public IReadOnlyDictionary<string, Customer> CustomerById { get; }

    public IReadOnlyDictionary<string, InvoiceRecord> InvoiceByNumber { get; }

    public IReadOnlyDictionary<string, Customer> CustomerByIban { get; }

    public Customer? CustomerOfPayer(BankPayment payment) =>
        CustomerByIban.GetValueOrDefault(NormalizeIban(payment.CounterpartyIban));

    public static string NormalizeIban(string iban) =>
        new(iban.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static MatchingDataset Load(string directory) => Parse(
        File.ReadAllText(Path.Combine(directory, "customers.csv"), Encoding.UTF8),
        File.ReadAllText(Path.Combine(directory, "invoices.csv"), Encoding.UTF8),
        File.ReadAllText(Path.Combine(directory, "bank_statement.csv"), Encoding.UTF8));

    /// <summary>The same three files as text (e.g. generated in memory for a new sandbox).</summary>
    public static MatchingDataset Parse(string customersCsv, string invoicesCsv, string statementCsv)
    {
        var customers = Read(customersCsv, 3)
            .Select(f => new Customer(f[0], f[1], f[2]))
            .ToList();
        var invoices = Read(invoicesCsv, 8)
            .Select(f => new InvoiceRecord(f[0], VariableSymbol.Normalize(f[1]), f[2], f[3], ParseDate(f[5]), LegacyCsv.ParseAmount(f[6]), f[7]))
            .ToList();
        var payments = ReadWithRaw(statementCsv, 10)
            .Select(r => new BankPayment(r.Fields[0], ParseDate(r.Fields[1]), LegacyCsv.ParseAmount(r.Fields[2]), r.Fields[3], r.Fields[4], r.Fields[5], r.Fields[6], r.Fields[9], r.Raw))
            .ToList();
        return new MatchingDataset(customers, invoices, payments);
    }

    private static DateOnly ParseDate(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : default;

    private static IEnumerable<List<string>> Read(string csv, int minFields) =>
        ReadWithRaw(csv, minFields).Select(r => r.Fields);

    private static IEnumerable<(List<string> Fields, string Raw)> ReadWithRaw(string csv, int minFields) =>
        csv
            .Split('\n')
            .Skip(1)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Select(line => (Fields: SplitCsv(line), Raw: line))
            .Where(r => r.Fields.Count >= minFields);

    /// <summary>RFC 4180 on one line, ';' separator.</summary>
    public static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes && c == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ';' && !inQuotes)
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
}
