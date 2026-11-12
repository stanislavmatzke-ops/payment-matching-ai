using Microsoft.EntityFrameworkCore;
using PaymentMatching.Matching;

namespace PaymentMatching.Data;

/// <summary>Reads the current tenant's data in the shape the matching code uses.</summary>
public static class TenantData
{
    public static async Task<MatchingDataset> LoadDatasetAsync(PaymentsDbContext db, CancellationToken cancellationToken = default)
    {
        var customers = await db.Customers.AsNoTracking().OrderBy(c => c.Id)
            .Select(c => new Customer(c.Id, c.Name, c.Iban))
            .ToListAsync(cancellationToken);
        var invoices = await db.Invoices.AsNoTracking().OrderBy(i => i.Number)
            .Select(i => new InvoiceRecord(i.Number, i.VariableSymbol, i.CustomerId, i.Kind, i.DueDate, i.Amount, i.Currency))
            .ToListAsync(cancellationToken);
        var payments = await db.Payments.AsNoTracking().OrderBy(p => p.Seq)
            .Select(p => new BankPayment(p.Id, p.BookingDate, p.Amount, p.Currency, p.CounterpartyIban, p.CounterpartyName, p.VariableSymbol, p.Message, p.RawLine))
            .ToListAsync(cancellationToken);
        return new MatchingDataset(customers, invoices, payments);
    }

    /// <summary>Adds a dataset to the current tenant (not saved yet).</summary>
    public static void Add(PaymentsDbContext db, MatchingDataset data)
    {
        db.Customers.AddRange(data.Customers.Select(c => new CustomerRow { Id = c.Id, Name = c.Name, Iban = c.Iban }));
        db.Invoices.AddRange(data.Invoices.Select(i => new InvoiceRow
        {
            Number = i.Number,
            VariableSymbol = i.VariableSymbol,
            CustomerId = i.CustomerId,
            Kind = i.Kind,
            DueDate = i.DueDate,
            Amount = i.Amount,
            Currency = i.Currency,
        }));
        db.Payments.AddRange(data.Payments.Select((p, seq) => new PaymentRow
        {
            Id = p.Id,
            Seq = seq,
            BookingDate = p.BookingDate,
            Amount = p.Amount,
            Currency = p.Currency,
            CounterpartyIban = p.CounterpartyIban,
            CounterpartyName = p.CounterpartyName,
            VariableSymbol = p.VariableSymbol,
            Message = p.Message,
            RawLine = p.RawLine,
        }));
    }
}

/// <summary>
/// The tenant's decisions for the matching pipeline. Appends are checked in memory (like the JSONL
/// log) and saved with the caller's <c>SaveChanges</c>; the primary keys of <c>decisions</c> and
/// <c>invoice_matches</c> enforce the same two rules in the database, also against a concurrent run.
/// </summary>
public sealed class DbDecisionStore : IDecisionStore
{
    private readonly PaymentsDbContext _db;
    private readonly DecisionLog _memory = new();

    private DbDecisionStore(PaymentsDbContext db) => _db = db;

    public IReadOnlyCollection<AuditRecord> Records => _memory.Records;

    public IReadOnlySet<string> MatchedInvoices => _memory.MatchedInvoices;

    public static async Task<DbDecisionStore> LoadAsync(PaymentsDbContext db, CancellationToken cancellationToken = default)
    {
        var store = new DbDecisionStore(db);
        var rows = await db.Decisions.AsNoTracking().OrderBy(d => d.DecidedAt).Select(d => d.Record).ToListAsync(cancellationToken);
        foreach (var json in rows)
        {
            store._memory.Append(DecisionLog.Deserialize(json));
        }

        return store;
    }

    public bool TryGet(string key, out AuditRecord record) => _memory.TryGet(key, out record);

    public void Append(AuditRecord record)
    {
        _memory.Append(record);
        _db.Decisions.Add(new DecisionRow
        {
            IdempotencyKey = record.IdempotencyKey,
            PaymentId = record.PaymentId,
            Outcome = record.Decision.Outcome.ToString(),
            DecidedAt = record.At,
            Record = DecisionLog.Serialize(record),
        });
        if (record.Decision.Outcome == Outcome.AutoMatched)
        {
            _db.InvoiceMatches.AddRange(record.Decision.InvoiceNumbers.Select(n => new InvoiceMatchRow
            {
                InvoiceNumber = n,
                PaymentId = record.PaymentId,
                DecisionKey = record.IdempotencyKey,
            }));
        }
    }
}

/// <summary>
/// The tenant's proposal queue for the MCP server. Each new proposal is saved at once; the primary
/// key (tenant, proposal id) keeps it idempotent even when two agents propose the same at the same time.
/// </summary>
public sealed class DbProposalStore : IProposalStore
{
    private readonly PaymentsDbContext _db;
    private readonly Dictionary<string, Proposal> _byId = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    private DbProposalStore(PaymentsDbContext db) => _db = db;

    public static async Task<DbProposalStore> LoadAsync(PaymentsDbContext db, CancellationToken cancellationToken = default)
    {
        var store = new DbProposalStore(db);
        foreach (var json in await db.Proposals.AsNoTracking().Select(p => p.Record).ToListAsync(cancellationToken))
        {
            var proposal = Proposal.Deserialize(json);
            store._byId[proposal.Id] = proposal;
        }

        return store;
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

            var row = new ProposalRow { Id = proposal.Id, PaymentId = proposal.PaymentId, At = proposal.At, Record = Proposal.Serialize(proposal) };
            _db.Proposals.Add(row);
            try
            {
                _db.SaveChanges();
            }
            catch (DbUpdateException)
            {
                // Another process queued the same proposal first: return that one.
                _db.Entry(row).State = EntityState.Detached;
                var stored = _db.Proposals.AsNoTracking().Single(p => p.Id == proposal.Id);
                existing = Proposal.Deserialize(stored.Record);
                _byId[existing.Id] = existing;
                return (existing, false);
            }

            _byId[proposal.Id] = proposal;
            return (proposal, true);
        }
    }
}
