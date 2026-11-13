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
/// The only writes a visitor can make (SPEC 9b): confirm a proposal, reject, match manually by picking
/// invoices, or undo a match the AI made on its own (ADR 0018). All of it goes through the tenant-filtered
/// <see cref="PaymentsDbContext"/>, so a payment or invoice of another tenant does not exist here. Nothing
/// calls a model and nothing takes free text. A match is validated by code like an agent's proposal;
/// failures a person may judge are shown and must be acknowledged one by one, the others block.
/// </summary>
public sealed class ReviewActions(PaymentsDbContext db, TimeProvider time, MatchingOptions? options = null)
{
    public async Task<ReviewResult> ConfirmAsync(string paymentId, string proposalId, IReadOnlyCollection<string> acknowledged, CancellationToken cancellationToken = default)
    {
        var open = await OpenAsync(paymentId, undo: false, cancellationToken);
        if (open.Refusal is not null)
        {
            return open.Refusal;
        }

        // The invoices come from the stored proposal, never from the request.
        var proposal = open.Proposals.SingleOrDefault(p => p.Id == proposalId);
        return proposal is null
            ? ReviewResult.Of(ReviewOutcome.UnknownProposal)
            : await CheckAndSaveAsync(open, ReviewAction.Confirmed, proposal.InvoiceNumbers, proposal.Id, acknowledged, cancellationToken);
    }

    public async Task<ReviewResult> MatchManuallyAsync(string paymentId, IReadOnlyList<string> invoiceNumbers, IReadOnlyCollection<string> acknowledged, CancellationToken cancellationToken = default)
    {
        var open = await OpenAsync(paymentId, undo: false, cancellationToken);
        return open.Refusal
            ?? (ValidInvoices(open.State, invoiceNumbers)
                ? await CheckAndSaveAsync(open, ReviewAction.MatchedManually, invoiceNumbers, null, acknowledged, cancellationToken)
                : ReviewResult.Of(ReviewOutcome.InvalidInvoices));
    }

    public async Task<ReviewResult> RejectAsync(string paymentId, CancellationToken cancellationToken = default)
    {
        var open = await OpenAsync(paymentId, undo: false, cancellationToken);
        return open.Refusal ?? await SaveAsync(open, ReviewAction.Rejected, [], null, [], [], cancellationToken);
    }

    /// <summary>
    /// Undoes a match the AI layer made on its own: frees its invoices and matches the payment to the invoices
    /// the person picks, or to none. The AI's decision stays in the audit trail; this is the person's decision.
    /// </summary>
    public async Task<ReviewResult> UndoAiMatchAsync(string paymentId, IReadOnlyList<string> invoiceNumbers, IReadOnlyCollection<string> acknowledged, CancellationToken cancellationToken = default)
    {
        var open = await OpenAsync(paymentId, undo: true, cancellationToken);
        if (open.Refusal is not null)
        {
            return open.Refusal;
        }

        if (invoiceNumbers.Count == 0)
        {
            return await SaveAsync(open, ReviewAction.UndoneAi, [], null, [], [], cancellationToken);
        }

        return ValidInvoices(open.State, invoiceNumbers)
            ? await CheckAndSaveAsync(open, ReviewAction.UndoneAi, invoiceNumbers, null, acknowledged, cancellationToken)
            : ReviewResult.Of(ReviewOutcome.InvalidInvoices);
    }

    /// <summary>The checks a manual match would run, without saving anything.</summary>
    public Task<ReviewResult> PreviewAsync(string paymentId, IReadOnlyList<string> invoiceNumbers, CancellationToken cancellationToken = default) =>
        PreviewAsync(paymentId, invoiceNumbers, undo: false, cancellationToken);

    /// <summary>The checks an undo with other invoices would run (the AI's invoices count as open), without saving anything.</summary>
    public Task<ReviewResult> PreviewUndoAsync(string paymentId, IReadOnlyList<string> invoiceNumbers, CancellationToken cancellationToken = default) =>
        PreviewAsync(paymentId, invoiceNumbers, undo: true, cancellationToken);

    private async Task<ReviewResult> PreviewAsync(string paymentId, IReadOnlyList<string> invoiceNumbers, bool undo, CancellationToken cancellationToken)
    {
        var open = await OpenAsync(paymentId, undo, cancellationToken);
        if (open.Refusal is not null)
        {
            return open.Refusal;
        }

        if (!ValidInvoices(open.State, invoiceNumbers))
        {
            return ReviewResult.Of(ReviewOutcome.InvalidInvoices);
        }

        var failures = open.Check(invoiceNumbers);
        return failures.Any(ProposalChecks.Blocking.Contains)
            ? new ReviewResult(ReviewOutcome.Blocked, failures)
            : new ReviewResult(failures.Count == 0 ? ReviewOutcome.Done : ReviewOutcome.NeedsAcknowledgement, failures);
    }

    private static bool ValidInvoices(SandboxState state, IReadOnlyList<string> invoiceNumbers) =>
        invoiceNumbers.Count is > 0 and <= ProposalChecks.MaxInvoices
        && invoiceNumbers.Distinct(StringComparer.Ordinal).Count() == invoiceNumbers.Count
        && invoiceNumbers.All(state.Data.InvoiceByNumber.ContainsKey);

    private async Task<OpenPayment> OpenAsync(string paymentId, bool undo, CancellationToken cancellationToken)
    {
        var state = await SandboxState.LoadAsync(db, options, cancellationToken);
        if (state.Payment(paymentId) is not { } payment)
        {
            return OpenPayment.Refused(state, ReviewOutcome.NotFound);
        }

        var refusal = state.Review(paymentId) is not null ? ReviewOutcome.AlreadyDecided
            : !(undo ? state.CanUndoAi(paymentId) : state.IsActionable(paymentId)) ? ReviewOutcome.NotActionable
            : (ReviewOutcome?)null;
        return refusal is { } outcome
            ? OpenPayment.Refused(state, outcome)
            // An undo frees the AI's invoices; the proposals shown before are not part of that decision.
            : new OpenPayment(state, payment, undo, undo ? [] : state.ProposalsFor(payment), null);
    }

    private async Task<ReviewResult> CheckAndSaveAsync(
        OpenPayment open,
        string action,
        IReadOnlyList<string> invoiceNumbers,
        string? proposalId,
        IReadOnlyCollection<string> acknowledged,
        CancellationToken cancellationToken)
    {
        var failures = open.Check(invoiceNumbers);
        if (failures.Any(ProposalChecks.Blocking.Contains))
        {
            return new ReviewResult(ReviewOutcome.Blocked, failures);
        }

        // The person must have seen and accepted exactly today's warnings (not an older, shorter list).
        if (!failures.ToHashSet(StringComparer.Ordinal).SetEquals(acknowledged))
        {
            return new ReviewResult(ReviewOutcome.NeedsAcknowledgement, failures);
        }

        return await SaveAsync(open, action, invoiceNumbers, proposalId, failures, acknowledged.Order(StringComparer.Ordinal).ToList(), cancellationToken);
    }

    private async Task<ReviewResult> SaveAsync(
        OpenPayment open,
        string action,
        IReadOnlyList<string> invoiceNumbers,
        string? proposalId,
        IReadOnlyList<string> checks,
        IReadOnlyList<string> acknowledged,
        CancellationToken cancellationToken)
    {
        var payment = open.Payment!;
        var userId = await db.Users.Select(u => u.Id).SingleAsync(cancellationToken);
        var key = DecisionLog.KeyFor(payment);
        var decision = new HumanDecision(
            payment.Id,
            action,
            invoiceNumbers,
            proposalId,
            checks,
            acknowledged,
            open.Proposals.Select(p => new ProposalSnapshot(p.Id, p.InvoiceNumbers, p.Sources.Select(s => s.ProposedBy).ToList())).ToList(),
            userId,
            time.GetUtcNow());
        var review = new ReviewRow { DecisionKey = key, PaymentId = payment.Id, Action = action, UserId = userId, At = decision.At, Record = HumanDecision.Serialize(decision) };
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (open.Undo)
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

    /// <summary>
    /// A payment loaded for a decision: either refused (<see cref="Refusal"/>), or open with the proposals the
    /// person saw. <see cref="Payment"/> is set whenever <see cref="Refusal"/> is null.
    /// </summary>
    private sealed record OpenPayment(SandboxState State, BankPayment? Payment, bool Undo, IReadOnlyList<QueueProposal> Proposals, ReviewResult? Refusal)
    {
        public static OpenPayment Refused(SandboxState state, ReviewOutcome outcome) => new(state, null, false, [], ReviewResult.Of(outcome));

        /// <summary>Today's checks; for an undo the payment's own invoices count as open.</summary>
        public IReadOnlyList<string> Check(IReadOnlyList<string> invoiceNumbers) =>
            State.Check(Payment!, invoiceNumbers, freeing: Undo ? Payment!.Id : null);
    }
}
