using System.Globalization;
using System.Text;
using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Csv;

/// <summary>Serializes a <see cref="Dataset"/> into the four CSV files described in docs/data-format.md.</summary>
public static class DatasetCsv
{
    public const string CustomersFile = "customers.csv";
    public const string InvoicesFile = "invoices.csv";
    public const string StatementFile = "bank_statement.csv";
    public const string ExpectedFile = "expected.csv";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static IReadOnlyDictionary<string, string> ToCsv(Dataset dataset) => new Dictionary<string, string>
    {
        [CustomersFile] = CsvWriter.Write(
            ["customer_id", "name", "iban"],
            dataset.Customers.Select(c => new[] { c.CustomerId, c.Name, c.Iban })),

        [InvoicesFile] = CsvWriter.Write(
            ["invoice_number", "variable_symbol", "customer_id", "kind", "issue_date", "due_date", "amount", "currency"],
            dataset.Invoices.Select(i => new[]
            {
                i.InvoiceNumber, i.VariableSymbol, i.CustomerId, i.Kind.ToString(), Date(i.IssueDate), Date(i.DueDate),
                Money(i.Amount), i.Currency,
            })),

        [StatementFile] = CsvWriter.Write(
            ["payment_id", "booking_date", "amount", "currency", "counterparty_iban", "counterparty_name",
             "variable_symbol", "constant_symbol", "specific_symbol", "message"],
            dataset.Transactions.Select(t => new[]
            {
                t.PaymentId, Date(t.BookingDate), Money(t.Amount), t.Currency, t.CounterpartyIban, t.CounterpartyName,
                t.VariableSymbol, t.ConstantSymbol, t.SpecificSymbol, t.Message,
            })),

        [ExpectedFile] = CsvWriter.Write(
            ["payment_id", "case_type", "expected_outcome", "invoice_numbers", "fx_rate", "injection_target", "note"],
            dataset.Expected.Select(e => new[]
            {
                e.PaymentId, e.CaseType.ToString(), e.Outcome.ToString(), string.Join('|', e.InvoiceNumbers),
                e.FxRate is { } rate ? rate.ToString("0.00", CultureInfo.InvariantCulture) : "",
                e.InjectionTarget ?? "", e.Note,
            })),
    };

    public static void WriteTo(Dataset dataset, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (fileName, content) in ToCsv(dataset))
        {
            File.WriteAllText(Path.Combine(directory, fileName), content, Utf8NoBom);
        }
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
