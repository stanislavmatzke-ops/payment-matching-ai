using System.Globalization;
using PaymentMatching.DataGen;
using PaymentMatching.DataGen.Csv;

// Usage: datagen [--seed 42] [--payments 300] [--date 2026-03-31] [--profile default|eval] [--out data/sample]
// --profile eval adds the hard case types of the eval gold set (GeneratorOptions.EvalCaseMix).
var options = new GeneratorOptions();
var output = "data/sample";

for (var i = 0; i < args.Length; i++)
{
    var value = i + 1 < args.Length ? args[i + 1] : null;
    switch (args[i])
    {
        case "--seed" when value is not null:
            options = options with { Seed = int.Parse(value, CultureInfo.InvariantCulture) };
            i++;
            break;
        case "--payments" when value is not null:
            options = options with { PaymentCount = int.Parse(value, CultureInfo.InvariantCulture) };
            i++;
            break;
        case "--date" when value is not null:
            options = options with { ReferenceDate = DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture) };
            i++;
            break;
        case "--profile" when value is "default" or "eval":
            options = options with { CaseMix = value == "eval" ? GeneratorOptions.EvalCaseMix : GeneratorOptions.DefaultCaseMix };
            i++;
            break;
        case "--out" when value is not null:
            output = value;
            i++;
            break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
            Console.Error.WriteLine("Usage: datagen [--seed 42] [--payments 300] [--date 2026-03-31] [--profile default|eval] [--out data/sample]");
            return 1;
    }
}

var dataset = DataGenerator.Generate(options);
DatasetCsv.WriteTo(dataset, output);

Console.WriteLine($"Seed {options.Seed}: {dataset.Customers.Count} customers, {dataset.Invoices.Count} invoices, " +
                  $"{dataset.Transactions.Count} payments -> {Path.GetFullPath(output)}");
foreach (var group in dataset.Expected.GroupBy(e => e.CaseType).OrderBy(g => g.Key))
{
    Console.WriteLine($"  {group.Key,-22} {group.Count(),4}");
}

return 0;
