using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Matching;

namespace PaymentMatching.Mcp;

/// <summary>
/// The data one MCP server process may see: one dataset directory (customers, invoices, bank
/// statement) and one state directory with the pipeline's audit log and the proposal queue.
/// The server reads the audit log but has no way to write it: the only write is a proposal for a
/// person (<see cref="ProposalQueue"/>). The workspace is fixed when the process starts; no tool
/// can choose another one: a dataset directory, or one tenant of the app database (ADR 0010, 0011).
/// </summary>
public sealed class Workspace
{
    public const string AuditFile = "audit.jsonl";
    public const string ProposalsFile = "proposals.jsonl";

    private readonly Dictionary<string, BankPayment> _paymentById;
    private readonly Dictionary<string, AuditRecord> _decisionByPayment;

    public Workspace(MatchingDataset data, IEnumerable<AuditRecord> decisions, IProposalStore proposals, MatchingOptions? options = null)
    {
        Data = data;
        Proposals = proposals;
        Options = options ?? MatchingOptions.Default;
        _paymentById = data.Payments.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Decisions are keyed by the statement line (idempotency key), so a decision recorded for
        // another statement that happens to reuse a payment id is never shown for this one.
        var byKey = decisions.ToDictionary(d => d.IdempotencyKey, StringComparer.Ordinal);
        _decisionByPayment = new Dictionary<string, AuditRecord>(StringComparer.Ordinal);
        foreach (var payment in _paymentById.Values)
        {
            if (byKey.TryGetValue(DecisionLog.KeyFor(payment), out var record))
            {
                _decisionByPayment[payment.Id] = record;
            }
        }

        MatchedInvoices = _decisionByPayment.Values
            .Where(r => r.Decision.Outcome == Outcome.AutoMatched)
            .SelectMany(r => r.Decision.InvoiceNumbers.Select(n => (Invoice: n, r.PaymentId)))
            .GroupBy(x => x.Invoice, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().PaymentId, StringComparer.Ordinal);
    }

    public MatchingDataset Data { get; }

    public IProposalStore Proposals { get; }

    public MatchingOptions Options { get; }

    /// <summary>Invoice number → payment it is matched to automatically.</summary>
    public IReadOnlyDictionary<string, string> MatchedInvoices { get; }

    public static Workspace Load(string dataDirectory, string stateDirectory)
    {
        var data = MatchingDataset.Load(dataDirectory);
        var auditPath = Path.Combine(stateDirectory, AuditFile);

        // Loaded read-only: the log is only parsed here and never appended to.
        var decisions = File.Exists(auditPath) ? new DecisionLog(auditPath).Records.ToList() : [];
        Directory.CreateDirectory(stateDirectory);
        return new Workspace(data, decisions, new ProposalQueue(Path.Combine(stateDirectory, ProposalsFile)));
    }

    /// <summary>
    /// The workspace of one tenant in the app database (phase 7). <paramref name="db"/> is bound to the tenant
    /// chosen when the process started, so every read is tenant-filtered by the data layer and proposals
    /// are saved under that tenant. The context lives as long as the server (it writes proposals).
    /// </summary>
    public static async Task<Workspace> LoadAsync(PaymentsDbContext db, CancellationToken cancellationToken = default)
    {
        if (!await db.Tenants.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException("The tenant does not exist (deleted after 30 days without use?).");
        }

        var data = await TenantData.LoadDatasetAsync(db, cancellationToken);

        // Read-only: the store is loaded for its records and never appended to.
        var decisions = (await DbDecisionStore.LoadAsync(db, cancellationToken)).Records.ToList();
        return new Workspace(data, decisions, await DbProposalStore.LoadAsync(db, cancellationToken));
    }

    public BankPayment? Payment(string id) => _paymentById.GetValueOrDefault(id.Trim());

    public AuditRecord? Decision(string paymentId) => _decisionByPayment.GetValueOrDefault(paymentId);

    public string Status(string paymentId) => PaymentStatus.Of(Decision(paymentId));

    public bool LooksLikeInjection(BankPayment payment) =>
        InjectionDetector.LooksLikeInjection($"{payment.Message} {payment.CounterpartyName} {payment.VariableSymbol}")
        || Decision(payment.Id)?.Decision.Flags.Contains(Flags.PromptInjection) == true;
}
