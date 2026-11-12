using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PaymentMatching.Matching;

/// <summary>
/// A match proposed by an agent. It is never a match: a person confirms or rejects it (phase 8 UI).
/// <see cref="Checks"/> are the code's validation failures at the time of the proposal (empty = all passed).
/// </summary>
public sealed record Proposal(
    string Id,
    string PaymentId,
    IReadOnlyList<string> InvoiceNumbers,
    string Reason,
    string ProposedBy,
    DateTimeOffset At,
    IReadOnlyList<string> Checks)
{
    public const string ByMcpAgent = "agent:mcp";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Same payment + same invoices (in any order) → same id.</summary>
    public static string IdFor(string paymentId, IEnumerable<string> invoiceNumbers)
    {
        var text = paymentId + "|" + string.Join(",", invoiceNumbers.Order(StringComparer.Ordinal));
        return "PR-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
    }

    public static string Serialize(Proposal proposal) => JsonSerializer.Serialize(proposal, Json);

    public static Proposal Deserialize(string json) =>
        JsonSerializer.Deserialize<Proposal>(json, Json) ?? throw new InvalidDataException("Bad proposal JSON.");
}

/// <summary>
/// Queue of agent proposals for a person. Idempotent: the same payment with the same set of
/// invoices is one proposal (<see cref="Proposal.IdFor"/>), however often an agent repeats it.
/// </summary>
public interface IProposalStore
{
    IReadOnlyList<Proposal> ForPayment(string paymentId);

    bool HasProposalFor(string paymentId) => ForPayment(paymentId).Count > 0;

    /// <summary>Adds the proposal, or returns the existing one with the same id (Created = false).</summary>
    (Proposal Proposal, bool Created) Add(Proposal proposal);
}

/// <summary>Append-only proposal queue in a JSON Lines file (local MCP server without a database).</summary>
public sealed class ProposalQueue : IProposalStore
{
    private readonly string? _path;
    private readonly Dictionary<string, Proposal> _byId = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <param name="path">JSONL file to load and append to; null keeps everything in memory.</param>
    public ProposalQueue(string? path = null)
    {
        _path = path;
        if (path is not null && File.Exists(path))
        {
            foreach (var line in File.ReadLines(path).Where(l => l.Length > 0))
            {
                var proposal = Proposal.Deserialize(line);
                _byId[proposal.Id] = proposal;
            }
        }
    }

    public IReadOnlyList<Proposal> ForPayment(string paymentId)
    {
        lock (_lock)
        {
            return _byId.Values.Where(p => p.PaymentId == paymentId).OrderBy(p => p.At).ToList();
        }
    }

    public (Proposal Proposal, bool Created) Add(Proposal proposal)
    {
        lock (_lock)
        {
            if (_byId.TryGetValue(proposal.Id, out var existing))
            {
                return (existing, false);
            }

            if (_path is not null)
            {
                File.AppendAllText(_path, Proposal.Serialize(proposal) + "\n", Encoding.UTF8);
            }

            _byId[proposal.Id] = proposal;
            return (proposal, true);
        }
    }
}
