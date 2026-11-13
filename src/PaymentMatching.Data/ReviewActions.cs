using Microsoft.EntityFrameworkCore;
using PaymentMatching.Matching;

namespace PaymentMatching.Data;

public enum ReviewOutcome
{
    Done,

    /// <summary>No such payment in this tenant (another tenant's payment is "not found" too).</summary>
    NotFound,

    /// <summary>A person has already decided this payment.</summary>
    AlreadyDecided,

    /// <summary>Matched automatically or waiting for the AI run: nothing for a person to decide.</summary>
    NotActionable,

    /// <summary>No proposal with that id for this payment.</summary>
    UnknownProposal,

    /// <summary>Invoices that are not this tenant's, repeated, or none / too many.</summary>
    InvalidInvoices,

    /// <summary>A check a person cannot override failed (<see cref="ProposalChecks.Blocking"/>).</summary>
    Blocked,

    /// <summary>Checks failed with warnings; the person has to acknowledge exactly these first.</summary>
    NeedsAcknowledgement,
}

public sealed record ReviewResult(ReviewOutcome Outcome, IReadOnlyList<string> Failures)
{
    public static ReviewResult Of(ReviewOutcome outcome) => new(outcome, []);
}

/// <summary>
/// The only writes a visitor can make (SPEC 9b): confirm a proposal, reject, or match manually by
/// picking invoices. All of it goes through the tenant-filtered <see cref="PaymentsDbContext"/>, so a
/// payment or invoice of another tenant does not exist here. Nothing calls a model and nothing takes
/// free text. A match is validated by code like an agent's proposal; failures a person may judge are
/// shown and must be acknowledged one by one, the others block.
/// </summary>
public sealed class ReviewActions(PaymentsDbContext db, TimeProvider time, MatchingOptions? options = null)
{
    public async Task<ReviewResult> ConfirmAsync(string paymentId, string proposalId, IReadOnlyCollection<string> acknowledged, CancellationToken cancellationToken = default)
    {
        var (state, payment, refusal) = await OpenAsync(paymentId, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        // The invoices come from the stored proposal, never from the request.
        var proposals = state.ProposalsFor(payment!);
        var proposal = proposals.SingleOrDefault(p => p.Id == proposalId);
        return proposal is null
            ? ReviewResult.Of(ReviewOutcome.UnknownProposal)
            : await MatchAsync(state, payment!, ReviewAction.Confirmed, proposal.InvoiceNumbers, proposal.Id, proposals, acknowledged, cancellationToken);
    }

    public async Task<ReviewResult> MatchManuallyAsync(string paymentId, IReadOnlyList<string> invoiceNumbers, IReadOnlyCollection<string> acknowledged, CancellationToken cancellationToken = default)
    {
        var (state, payment, refusal) = await OpenAsync(paymentId, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!ValidInvoices(state, invoiceNumbers))
        {
            return ReviewResult.Of(ReviewOutcome.InvalidInvoices);
        }

        return await MatchAsync(state, payment!, ReviewAction.MatchedManually, invoiceNumbers, null, state.ProposalsFor(payment!), acknowledged, cancellationToken);
    }

    public async Task<ReviewResult> RejectAsync(string paymentId, CancellationToken cancellationToken = default)
    {
        var (state, payment, refusal) = await OpenAsync(paymentId, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        return await SaveAsync(payment!, ReviewAction.Rejected, [], null, [], [], state.ProposalsFor(payment!), cancellationToken);
    }

    /// <summary>
    /// Undoes a match the AI layer made on its own: frees its invoices and matches the payment to the invoices
    /// the person picks, or to none. The AI's decision stays in the audit trail; this is the person's decision.
    /// </summary>
    public async Task<ReviewResult> UndoAiMatchAsync(string paymentId, IReadOnlyList<string> invoiceNumbers, IReadOnlyCollection<string> acknowledged, CancellationToken cancellationToken = default)
    {
        var (state, payment, refusal) = await OpenAsync(paymentId, cancellationToken, undo: true);
        if (refusal is not null)
        {
            return refusal;
        }

        if (invoiceNumbers.Count == 0)
        {
            return await SaveAsync(payment!, ReviewAction.UndoneAi, [], null, [], [], [], cancellationToken, freeing: true);
        }

        if (!ValidInvoices(state, invoiceNumbers))
        {
            return ReviewResult.Of(ReviewOutcome.InvalidInvoices);
        }

        var failures = state.Check(payment!, invoiceNumbers, freeing: paymentId);
        if (failures.Any(ProposalChecks.Blocking.Contains))
        {
            return new ReviewResult(ReviewOutcome.Blocked, failures);
        }

        if (!failures.ToHashSet(StringComparer.Ordinal).SetEquals(acknowledged))
        {
            return new ReviewResult(ReviewOutcome.NeedsAcknowledgement, failures);
        }

        return await SaveAsync(payment!, ReviewAction.UndoneAi, invoiceNumbers, null, failures, acknowledged.Order(StringComparer.Ordinal).ToList(), [], cancellationToken, freeing: true);
    }

    /// <summary>The checks a manual match (or an undo with other invoices) would run, without saving anything.</summary>
    public async Task<ReviewResult> PreviewAsync(string paymentId, IReadOnlyList<string> invoiceNumbers, CancellationToken cancellationToken = default, bool undo = false)
    {
        var (state, payment, refusal) = await OpenAsync(paymentId, cancellationToken, undo);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!ValidInvoices(state, invoiceNumbers))
        {
            return ReviewResult.Of(ReviewOutcome.InvalidInvoices);
        }

        var failures = state.Check(payment!, invoiceNumbers, undo ? paymentId : null);
        return failures.Any(ProposalChecks.Blocking.Contains)
            ? new ReviewResult(ReviewOutcome.Blocked, failures)
            : new ReviewResult(failures.Count == 0 ? ReviewOutcome.Done : ReviewOutcome.NeedsAcknowledgement, failures);
    }

    private static bool ValidInvoices(SandboxState state, IReadOnlyList<string> invoiceNumbers) =>
        invoiceNumbers.Count is > 0 and <= ProposalChecks.MaxInvoices
        && invoiceNumbers.Distinct(StringComparer.Ordinal).Count() == invoiceNumbers.Count
        && invoiceNumbers.All(state.Data.InvoiceByNumber.ContainsKey);

    private async Task<(SandboxState State, BankPayment? Payment, ReviewResult? Refusal)> OpenAsync(string paymentId, CancellationToken cancellationToken, bool undo = false)
    {
        var state = await SandboxState.LoadAsync(db, options, cancellationToken);
        var payment = state.Payment(paymentId);
        ReviewResult? refusal = payment is null ? ReviewResult.Of(ReviewOutcome.NotFound)
            : state.Review(paymentId) is not null ? ReviewResult.Of(ReviewOutcome.AlreadyDecided)
            : !(undo ? state.CanUndoAi(paymentId) : state.IsActionable(paymentId)) ? ReviewResult.Of(ReviewOutcome.NotActionable)
            : null;
        return (state, payment, refusal);
    }

    private async Task<ReviewResult> MatchAsync(
        SandboxState state,
        BankPayment payment,
        string action,
        IReadOnlyList<string> invoiceNumbers,
        string? proposalId,
        IReadOnlyList<QueueProposal> proposals,
        IReadOnlyCollection<string> acknowledged,
        CancellationToken cancellationToken)
    {
        var failures = state.Check(payment, invoiceNumbers);
        if (failures.Any(ProposalChecks.Blocking.Contains))
        {
            return new ReviewResult(ReviewOutcome.Blocked, failures);
        }

        // The person must have seen and accepted exactly today's warnings (not an older, shorter list).
        if (!failures.ToHashSet(StringComparer.Ordinal).SetEquals(acknowledged))
        {
            return new ReviewResult(ReviewOutcome.NeedsAcknowledgement, failures);
        }

        return await SaveAsync(payment, action, invoiceNumbers, proposalId, failures, acknowledged.Order(StringComparer.Ordinal).ToList(), proposals, cancellationToken);
    }

    private async Task<ReviewResult> SaveAsync(
        BankPayment payment,
        string action,
        IReadOnlyList<string> invoiceNumbers,
        string? proposalId,
        IReadOnlyList<string> checks,
        IReadOnlyList<string> acknowledged,
        IReadOnlyList<QueueProposal> proposals,
        CancellationToken cancellationToken,
        bool freeing = false)
    {
        var userId = await db.Users.Select(u => u.Id).SingleAsync(cancellationToken);
        var key = DecisionLog.KeyFor(payment);
        var decision = new HumanDecision(
            payment.Id,
            action,
            invoiceNumbers,
            proposalId,
            checks,
            acknowledged,
            proposals.Select(p => new ProposalSnapshot(p.Id, p.InvoiceNumbers, p.Sources.Select(s => s.ProposedBy).ToList())).ToList(),
            userId,
            time.GetUtcNow());
        var review = new ReviewRow { DecisionKey = key, PaymentId = payment.Id, Action = action, UserId = userId, At = decision.At, Record = HumanDecision.Serialize(decision) };
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (freeing)
        {
            // Undoing an AI match: its invoices become open again, in the same save as the person's decision
            // (an invoice the person picks again simply stays matched to this payment).
            var current = await db.InvoiceMatches.Where(m => m.PaymentId == payment.Id).ToListAsync(cancellationToken);
            db.InvoiceMatches.RemoveRange(current.Where(m => !invoiceNumbers.Contains(m.InvoiceNumber)));
            keep.UnionWith(current.Select(m => m.InvoiceNumber));
        }

        var matches = invoiceNumbers.Where(n => !keep.Contains(n)).Select(n => new InvoiceMatchRow { InvoiceNumber = n, PaymentId = payment.Id, DecisionKey = key }).ToList();

        db.Reviews.Add(review);
        db.InvoiceMatches.AddRange(matches);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request decided the payment or matched one of the invoices first (primary keys).
            db.Entry(review).State = EntityState.Detached;
            matches.ForEach(m => db.Entry(m).State = EntityState.Detached);
            return await db.Reviews.AnyAsync(r => r.DecisionKey == key, cancellationToken)
                ? ReviewResult.Of(ReviewOutcome.AlreadyDecided)
                : new ReviewResult(ReviewOutcome.Blocked, [Rejections.AlreadyMatched]);
        }

        return ReviewResult.Of(ReviewOutcome.Done);
    }
}
