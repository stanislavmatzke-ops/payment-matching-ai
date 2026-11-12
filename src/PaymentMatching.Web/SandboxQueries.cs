using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Matching;

namespace PaymentMatching.Web;

public sealed record SandboxCounts(int Customers, int Invoices, int Payments, int AutoMatched, int HumanReview, int NoMatch, int WaitingForAi);

public sealed record MeView(string Name, string Email, string Provider, Guid TenantId, DateTimeOffset SandboxCreatedAt, SandboxCounts Counts);

/// <summary>Bank statement text is untrusted (written by the payer); JSON encoding escapes it for HTML.</summary>
public sealed record PaymentListItem(
    string Id,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    string CounterpartyName,
    string VariableSymbol,
    string Message,
    string Status,
    string? DecidedBy,
    IReadOnlyList<string> Flags);

public sealed record PaymentDecisionView(
    string Status,
    string DecidedBy,
    string Layer,
    string? RuleCode,
    string? PromptVersion,
    double? Confidence,
    IReadOnlyList<string> InvoiceNumbers,
    string Reason,
    IReadOnlyList<string> ValidationFailures);

public sealed record PaymentDetailView(PaymentListItem Payment, PaymentDecisionView? Decision, IReadOnlyList<Proposal> Proposals);

/// <summary>
/// Read-only views of the signed-in person's sandbox. Every query runs through the tenant-filtered
/// <see cref="PaymentsDbContext"/>; nothing here names a tenant.
/// </summary>
public static class SandboxQueries
{
    public static async Task<MeView> MeAsync(PaymentsDbContext db, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var outcomes = await db.Decisions.GroupBy(d => d.Outcome).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        int Count(Outcome outcome) => outcomes.SingleOrDefault(o => o.Key == outcome.ToString())?.Count ?? 0;
        var payments = await db.Payments.CountAsync(cancellationToken);
        var counts = new SandboxCounts(
            await db.Customers.CountAsync(cancellationToken),
            await db.Invoices.CountAsync(cancellationToken),
            payments,
            Count(Outcome.AutoMatched),
            Count(Outcome.HumanReview),
            Count(Outcome.NoMatch),
            payments - outcomes.Sum(o => o.Count));
        return new MeView(user.Name, user.Email, user.Provider, user.TenantId, tenant.CreatedAt, counts);
    }

    public static async Task<IReadOnlyList<PaymentListItem>> PaymentsAsync(PaymentsDbContext db, CancellationToken cancellationToken)
    {
        var decisions = await DecisionsByPaymentAsync(db, null, cancellationToken);
        var payments = await db.Payments.AsNoTracking().OrderBy(p => p.Seq).ToListAsync(cancellationToken);
        return payments.Select(p => Item(p, decisions.GetValueOrDefault(p.Id))).ToList();
    }

    public static async Task<PaymentDetailView?> PaymentAsync(PaymentsDbContext db, string id, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (payment is null)
        {
            return null;
        }

        var record = (await DecisionsByPaymentAsync(db, id, cancellationToken)).GetValueOrDefault(id);
        var proposals = (await db.Proposals.AsNoTracking().Where(p => p.PaymentId == id).OrderBy(p => p.At).Select(p => p.Record).ToListAsync(cancellationToken))
            .Select(Proposal.Deserialize)
            .ToList();
        var decision = record is null ? null : new PaymentDecisionView(
            PaymentStatus.Of(record),
            record.Decision.DecidedBy,
            record.Layer,
            record.RuleCode,
            record.PromptVersion,
            record.Decision.Confidence,
            record.Decision.InvoiceNumbers,
            record.Decision.Reason,
            record.ValidationFailures);
        return new PaymentDetailView(Item(payment, record), decision, proposals);
    }

    private static async Task<Dictionary<string, AuditRecord>> DecisionsByPaymentAsync(PaymentsDbContext db, string? paymentId, CancellationToken cancellationToken)
    {
        // A decision belongs to the exact statement line (idempotency key), not just to a payment id.
        var keys = await db.Payments.AsNoTracking()
            .Where(p => paymentId == null || p.Id == paymentId)
            .Select(p => new { p.Id, p.RawLine })
            .ToListAsync(cancellationToken);
        var keyToPayment = keys.ToDictionary(k => DecisionLog.KeyFor(new BankPayment(k.Id, default, 0, "", "", "", "", "", k.RawLine)), k => k.Id, StringComparer.Ordinal);
        var records = await db.Decisions.AsNoTracking()
            .Where(d => paymentId == null || d.PaymentId == paymentId)
            .Select(d => new { d.IdempotencyKey, d.Record })
            .ToListAsync(cancellationToken);
        return records
            .Where(r => keyToPayment.ContainsKey(r.IdempotencyKey))
            .ToDictionary(r => keyToPayment[r.IdempotencyKey], r => DecisionLog.Deserialize(r.Record), StringComparer.Ordinal);
    }

    private static PaymentListItem Item(PaymentRow p, AuditRecord? record) => new(
        p.Id,
        p.BookingDate,
        p.Amount,
        p.Currency,
        p.CounterpartyName,
        p.VariableSymbol,
        p.Message,
        PaymentStatus.Of(record),
        record?.Decision.DecidedBy,
        record?.Decision.Flags ?? []);
}
