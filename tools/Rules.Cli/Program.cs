using PaymentMatching.Rules;

// Runs the .NET rules on a directory with invoices.csv and bank_statement.csv and writes the
// result in the legacy format – the .NET counterpart of legacy/run-legacy.sh.
// Usage: rules <input-dir> <output-file>
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: rules <input-dir> <output-file>");
    return 2;
}

var invoices = LegacyCsv.ReadInvoices(Path.Combine(args[0], LegacyCsv.InvoicesFile));
var payments = LegacyCsv.ReadPayments(Path.Combine(args[0], LegacyCsv.StatementFile));
var results = PaymentMatcher.Match(invoices, payments);

LegacyCsv.WriteText(args[1], LegacyCsv.WriteResults(results));

Console.WriteLine($"RULES: {invoices.Count} invoices, {payments.Count} payments");
foreach (var group in results.GroupBy(r => r.Status).OrderBy(g => g.Key))
{
    Console.WriteLine($"  {LegacyCsv.StatusCode(group.Key)} {group.Key,-8} {group.Count()}");
}

return 0;
