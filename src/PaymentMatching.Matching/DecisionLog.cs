using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using PaymentMatching.AiGateway;

namespace PaymentMatching.Matching;

/// <summary>
/// One audit entry per payment: input key, who decided and how, the model calls with their raw
/// output, tokens and cost, the candidates and the validation result.
/// </summary>
public sealed record AuditRecord(
    string IdempotencyKey,
    string PaymentId,
    DateTimeOffset At,
    string Layer,
    string? RuleCode,
    string? PromptVersion,
    Decision Decision,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> ValidationFailures,
    IReadOnlyList<UsageRecord> Calls)
{
    public const string RuleLayer = "rule";
    public const string AiLayer = "ai";
    public const string PolicyLayer = "policy";
}

/// <summary>
/// Append-only decision store and audit log (JSON Lines). It is also the idempotency guard:
/// a payment line is decided once, and an invoice is auto-matched to at most one payment.
/// In the hosted app (phase 7) the same two rules become unique indexes in PostgreSQL.
/// </summary>
public sealed class DecisionLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string? _path;
    private readonly Dictionary<string, AuditRecord> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _matchedInvoices = new(StringComparer.Ordinal);

    /// <param name="path">JSONL file to load and append to; null keeps everything in memory.</param>
    public DecisionLog(string? path = null)
    {
        _path = path;
        if (path is not null && File.Exists(path))
        {
            foreach (var line in File.ReadLines(path).Where(l => l.Length > 0))
            {
                Index(JsonSerializer.Deserialize<AuditRecord>(line, Json) ?? throw new InvalidDataException($"Bad audit line in {path}."));
            }
        }
    }

    public IReadOnlyCollection<AuditRecord> Records => _byKey.Values;

    /// <summary>Invoices already matched automatically (in this run or an earlier one).</summary>
    public IReadOnlySet<string> MatchedInvoices => _matchedInvoices.Keys.ToHashSet(StringComparer.Ordinal);

    public static string KeyFor(BankPayment payment) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payment.RawLine)));

    public bool TryGet(string key, out AuditRecord record) => _byKey.TryGetValue(key, out record!);

    public void Append(AuditRecord record)
    {
        if (_byKey.ContainsKey(record.IdempotencyKey))
        {
            throw new InvalidOperationException($"Payment {record.PaymentId} is already decided (key {record.IdempotencyKey}).");
        }

        if (record.Decision.Outcome == Outcome.AutoMatched)
        {
            var taken = record.Decision.InvoiceNumbers.FirstOrDefault(_matchedInvoices.ContainsKey);
            if (taken is not null)
            {
                throw new InvalidOperationException($"Invoice {taken} is already matched to payment {_matchedInvoices[taken]}.");
            }
        }

        if (_path is not null)
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(record, Json) + "\n", Encoding.UTF8);
        }

        Index(record);
    }

    private void Index(AuditRecord record)
    {
        _byKey[record.IdempotencyKey] = record;
        if (record.Decision.Outcome == Outcome.AutoMatched)
        {
            foreach (var invoice in record.Decision.InvoiceNumbers)
            {
                _matchedInvoices[invoice] = record.PaymentId;
            }
        }
    }
}
