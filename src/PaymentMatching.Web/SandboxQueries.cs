using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Matching;

namespace PaymentMatching.Web;

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
        return new MeView(user.Name, user.Email, user.Provider, $"{user.Provider}:{user.Subject}", user.TenantId, tenant.CreatedAt, counts);
    }

    public static async Task<IReadOnlyList<PaymentListItem>> PaymentsAsync(PaymentsDbContext db, CancellationToken cancellationToken)
    {
        var state = await SandboxState.LoadAsync(db, null, cancellationToken);
        return Items(state);
    }

    public static IReadOnlyList<PaymentListItem> Items(SandboxState state) => state.Data.Payments.Select(p => Item(state, p)).ToList();

    public static async Task<PaymentDetailView?> PaymentAsync(PaymentsDbContext db, string id, CancellationToken cancellationToken)
    {
        var state = await SandboxState.LoadAsync(db, null, cancellationToken);
        return Detail(state, id, await db.Users.AsNoTracking().Select(u => u.Name).SingleAsync(cancellationToken));
    }

    public static PaymentDetailView? Detail(SandboxState state, string id, string personName)
    {
        var payment = state.Payment(id);
        if (payment is null)
        {
            return null;
        }

        var record = state.Decision(id);
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
        var review = state.Review(id) is { } r
            ? new ReviewView(r.Action, r.InvoiceNumbers, r.ProposalId, personName, r.At, r.Checks, r.Acknowledged)
            : null;
        var agentProposals = state.AgentProposals(id);
        var queue = state.ProposalsFor(payment);
        var (invoices, comparisons) = Invoices(
            state,
            payment,
            new[] { record?.Decision.InvoiceNumbers, review?.InvoiceNumbers }
                .Concat(queue.Select(q => q.InvoiceNumbers))
                .Concat(agentProposals.Select(p => p.InvoiceNumbers)));
        return new PaymentDetailView(
            Item(state, payment),
            decision,
            agentProposals,
            review,
            state.IsActionable(id),
            queue,
            PaymentTimeline.For(state, payment, review),
            invoices,
            comparisons)
        {
            CanUndoAi = state.CanUndoAi(id),
        };
    }

    /// <summary>
    /// The invoices the decision, the review and the proposals name – only those of this sandbox, through its
    /// filtered data – and each named set against the payment amount.
    /// </summary>
    private static (IReadOnlyList<InvoiceFacts>, IReadOnlyList<InvoiceComparison>) Invoices(
        SandboxState state, BankPayment payment, IEnumerable<IReadOnlyList<string>?> namedSets)
    {
        var sets = namedSets
            .Where(s => s is { Count: > 0 })
            .Select(s => s!.Where(state.Data.InvoiceByNumber.ContainsKey).ToList())
            .Where(s => s.Count > 0)
            .DistinctBy(s => string.Join('\n', s))
            .ToList();
        var invoices = sets.SelectMany(s => s).Distinct(StringComparer.Ordinal)
            .Select(n => state.Data.InvoiceByNumber[n])
            .Select(i => new InvoiceFacts(
                i.Number,
                i.Kind,
                i.VariableSymbol,
                i.CustomerId,
                state.Data.CustomerById.TryGetValue(i.CustomerId, out var c) ? c.Name : "",
                i.DueDate,
                i.Amount,
                i.Currency,
                state.MatchedInvoices.TryGetValue(i.Number, out var paymentId) ? paymentId : null))
            .ToList();
        var comparisons = sets.Select(s =>
        {
            var named = s.Select(n => state.Data.InvoiceByNumber[n]).ToList();
            var currency = named[0].Currency;
            var sameCurrency = named.All(i => i.Currency == currency) && currency == payment.Currency;
            var total = named.Sum(i => i.Amount);
            return new InvoiceComparison(s, total, currency, sameCurrency ? payment.Amount - total : null);
        }).ToList();
        return (invoices, comparisons);
    }

    /// <summary>Payments a person should look at, oldest first.</summary>
    public static IReadOnlyList<PaymentListItem> Queue(SandboxState state) =>
        Items(state).Where(p => p.State == PaymentState.NeedsReview).ToList();

    public static IReadOnlyList<InvoiceOption> OpenInvoices(SandboxState state, string? freeing = null) => state.OpenInvoices(freeing)
        .Select(i => new InvoiceOption(i.Number, i.VariableSymbol, i.CustomerId, state.Data.CustomerById.TryGetValue(i.CustomerId, out var c) ? c.Name : "", i.DueDate, i.Amount, i.Currency))
        .ToList();

    public static DashboardView Dashboard(SandboxState state)
    {
        var states = state.Data.Payments.Select(p => state.StateOf(p.Id)).ToList();
        int Count(string s) => states.Count(x => x == s);
        var decisions = state.Decisions.ToList();
        var calls = decisions.SelectMany(d => d.Calls.Where(c => !c.Cached).Select(c => (Day: DateOnly.FromDateTime(d.At.UtcDateTime), c.CostUsd))).ToList();
        var costByDay = calls.GroupBy(c => c.Day).OrderBy(g => g.Key).Select(g => new DailyCost(g.Key, g.Count(), g.Sum(c => c.CostUsd))).ToList();
        return new DashboardView(
            state.Data.Payments.Count,
            decisions.Count,
            decisions.Count(d => d.Decision.Outcome == Outcome.AutoMatched),
            Count(PaymentState.NeedsReview),
            Count(PaymentState.NotMatched),
            Count(PaymentState.Rejected),
            Count(PaymentState.WaitingForAi),
            state.Reviews.Count(),
            decisions.Count(d => d.Decision.Outcome != Outcome.AutoMatched && d.Decision.Flags.Contains(Flags.PromptInjection)),
            calls.Count,
            calls.Sum(c => c.CostUsd),
            costByDay,
            LivePrecisionOf(state.Reviews))
        {
            CachedAnswers = decisions.Sum(d => d.Calls.Count(c => c.Cached)),
            AiBudgetUsedUp = decisions.Count(d => d.Decision.Flags.Contains(Flags.AiBudgetUsedUp) && state.StateOf(d.PaymentId) == PaymentState.NeedsReview),
            AiDecided = decisions.Count(d => d.Layer == AuditRecord.AiLayer),
            AiAutoMatched = decisions.Count(d => d.Layer == AuditRecord.AiLayer && d.Decision.Outcome == Outcome.AutoMatched),
            AiProposed = decisions.Count(d => d.Layer == AuditRecord.AiLayer && d.Decision.Outcome == Outcome.HumanReview && d.Decision.InvoiceNumbers.Count > 0),
            AiUndone = state.Reviews.Count(r => r.Action == ReviewAction.UndoneAi),
        };
    }

    /// <summary>
    /// For every proposal a person saw when deciding: accepted when the person matched exactly its
    /// invoices (confirmed it, or picked the same ones manually); otherwise not. Per proposer kind.
    /// </summary>
    public static IReadOnlyList<LivePrecision> LivePrecisionOf(IEnumerable<HumanDecision> reviews)
    {
        var votes = reviews.SelectMany(r => r.Proposals.SelectMany(p => p.ProposedBy.Distinct().Select(by => (
            Source: SourceOf(by),
            Accepted: r.Action != ReviewAction.Rejected && r.InvoiceNumbers.Count == p.InvoiceNumbers.Count && r.InvoiceNumbers.All(p.InvoiceNumbers.Contains)))));
        return votes.GroupBy(v => v.Source)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new LivePrecision(g.Key, g.Count(), g.Count(v => v.Accepted)))
            .ToList();
    }

    /// <summary>"rule:R3" → "rule", "ai:claude-…" → "ai", "agent:mcp" stays (each agent counts on its own).</summary>
    public static string SourceOf(string proposedBy) =>
        DecidedBy.KindOf(proposedBy) is DecidedBy.AgentKind ? proposedBy : DecidedBy.KindOf(proposedBy);

    private static PaymentListItem Item(SandboxState state, BankPayment p)
    {
        var record = state.Decision(p.Id);
        return new PaymentListItem(
            p.Id,
            p.BookingDate,
            p.Amount,
            p.Currency,
            p.CounterpartyName,
            p.VariableSymbol,
            p.Message,
            PaymentStatus.Of(record),
            state.Review(p.Id) is null ? record?.Decision.DecidedBy : DecidedBy.Person,
            record?.Decision.Flags ?? [],
            state.StateOf(p.Id),
            state.InjectionSuspected(p));
    }
}
