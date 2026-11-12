using System.Security.Cryptography;
using PaymentMatching.Matching;

namespace PaymentMatching.Evals;

/// <summary>Ground truth for one payment (a row of expected.csv).</summary>
/// <param name="Invoices">Correct invoices, sorted; empty when the payment must not be matched.</param>
/// <param name="InjectionTarget">Invoice an injected instruction asks for; it must never be matched or proposed.</param>
public sealed record GoldCase(string PaymentId, string CaseType, string ExpectedOutcome, IReadOnlyList<string> Invoices, string? InjectionTarget)
{
    public const string AutoMatch = "AutoMatch";
    public const string HumanReview = "HumanReview";
    public const string NoMatch = "NoMatch";

    public bool IsInjection => InjectionTarget is not null;
}

/// <summary>
/// A frozen eval dataset: the generator CSV files plus <c>expected.csv</c> (ground truth) and
/// <c>MANIFEST.sha256</c>, which pins the reviewed content (ADR 0008).
/// </summary>
public sealed class GoldSet
{
    public const string ManifestFile = "MANIFEST.sha256";

    private GoldSet(string name, MatchingDataset data, IReadOnlyDictionary<string, GoldCase> cases)
    {
        Name = name;
        Data = data;
        Cases = cases;
    }

    public string Name { get; }

    public MatchingDataset Data { get; }

    public IReadOnlyDictionary<string, GoldCase> Cases { get; }

    public static GoldSet From(string name, MatchingDataset data, IEnumerable<GoldCase> cases) =>
        new(name, data, cases.ToDictionary(c => c.PaymentId));

    public static GoldSet Load(string directory)
    {
        var data = MatchingDataset.Load(directory);
        var cases = File.ReadAllLines(Path.Combine(directory, "expected.csv"))
            .Skip(1)
            .Where(l => l.Length > 0)
            .Select(MatchingDataset.SplitCsv)
            .Select(f => new GoldCase(
                f[0], f[1], f[2],
                f[3].Split('|', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToList(),
                f[5].Length > 0 ? f[5] : null))
            .ToDictionary(c => c.PaymentId);

        var missing = data.Payments.Where(p => !cases.ContainsKey(p.Id)).Select(p => p.Id).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException($"No ground truth for payments {string.Join(", ", missing)}.");
        }

        return new GoldSet(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), data, cases);
    }

    /// <summary>Lines of <see cref="ManifestFile"/>: SHA-256 and file name of every CSV file, sorted by name.</summary>
    public static string ComputeManifest(string directory) =>
        string.Concat(Directory.GetFiles(directory, "*.csv")
            .Order(StringComparer.Ordinal)
            .Select(path => $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))}  {Path.GetFileName(path)}\n"));

    /// <summary>Empty when the files match the manifest; otherwise what differs.</summary>
    public static IReadOnlyList<string> VerifyManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestFile);
        if (!File.Exists(path))
        {
            return [$"{ManifestFile} is missing."];
        }

        var expected = File.ReadAllLines(path).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
        var actual = ComputeManifest(directory).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return expected.Except(actual).Select(l => "changed or missing: " + l[66..])
            .Concat(actual.Except(expected).Select(l => "not in manifest: " + l[66..]))
            .Distinct()
            .ToList();
    }
}
