using System.Globalization;
using PaymentMatching.Data;
using PaymentMatching.Matching;
using static PaymentMatching.Web.Text;

namespace PaymentMatching.Web;

/// <summary>
/// The audit trail of one payment as a person reads it: booked, model calls (or cached answers), the
/// decision with its details, agent proposals and the person's decision, oldest first, in the request's language.
/// </summary>
public static class PaymentTimeline
{
    public static IReadOnlyList<TimelineEvent> For(SandboxState state, BankPayment payment, ReviewView? review)
    {
        var events = new List<TimelineEvent>
        {
            new(new DateTimeOffset(payment.BookingDate, TimeOnly.MinValue, TimeSpan.Zero), TimelineKind.Received, "Demo Bank", T("Payment booked on the account."), [], []),
        };

        if (state.Decision(payment.Id) is { } record)
        {
            foreach (var call in record.Calls)
            {
                if (call.Cached)
                {
                    events.Add(new TimelineEvent(record.At, TimelineKind.ModelCall, DecidedBy.Ai(call.Model), T("Answer reused from the response cache ({0}): {1}. The same request was answered before; no model was called and it cost nothing.", call.Tier, call.Outcome), [], [], "cache"));
                    continue;
                }

                events.Add(new TimelineEvent(
                    record.At,
                    TimelineKind.ModelCall,
                    DecidedBy.Ai(call.Model),
                    T("Model call ({0}): {1}.", call.Tier, call.Outcome),
                    [],
                    [
                        T("Tokens in/out: {0} / {1}", call.Usage.InputTokens.ToString(CultureInfo.InvariantCulture), call.Usage.OutputTokens.ToString(CultureInfo.InvariantCulture)),
                        T("Cost: {0}", "$" + call.CostUsd.ToString("0.000000", CultureInfo.InvariantCulture)),
                        T("Latency: {0} s", call.Latency.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)),
                    ],
                    "ai"));
            }

            var details = new List<string> { T("Layer: {0}", record.Layer) };
            if (record.RuleCode is not null)
            {
                details.Add(T("Rule: {0} – {1}", record.RuleCode, Format.Rule(record.RuleCode)));
            }

            if (record.PromptVersion is not null)
            {
                details.Add(T("Prompt: {0}", record.PromptVersion));
            }

            if (record.Decision.Confidence is { } confidence)
            {
                details.Add(T("Confidence: {0}", confidence.ToString("0.00", CultureInfo.InvariantCulture)));
            }

            if (record.Candidates.Count > 0)
            {
                details.Add(T("Candidates: {0}", string.Join(", ", record.Candidates)));
            }

            if (record.ValidationFailures.Count > 0)
            {
                details.Add(T("Validation failed: {0}", string.Join(", ", record.ValidationFailures)));
            }

            if (record.Decision.Flags.Count > 0)
            {
                details.Add(T("Flags: {0}", string.Join(", ", record.Decision.Flags.Select(Format.Flag))));
            }

            details.Add(T("Reason: {0}", record.Decision.Reason));
            events.Add(new TimelineEvent(record.At, TimelineKind.Decided, record.Decision.DecidedBy, OutcomeText(record.Decision.Outcome), record.Decision.InvoiceNumbers, details));
        }

        foreach (var proposal in state.AgentProposals(payment.Id))
        {
            events.Add(new TimelineEvent(
                proposal.At,
                TimelineKind.Proposed,
                proposal.ProposedBy,
                T("Proposed a match for a person to review."),
                proposal.InvoiceNumbers,
                [
                    proposal.Checks.Count == 0 ? T("Checks at the time: all passed") : T("Checks at the time: {0}", string.Join(", ", proposal.Checks)),
                    T("Agent's reason: {0}", proposal.Reason),
                ]));
        }

        if (review is not null)
        {
            var details = new List<string>();
            if (review.Checks.Count > 0)
            {
                details.Add(T("Checks at the time: {0}", string.Join(", ", review.Checks)));
            }

            if (review.Acknowledged.Count > 0)
            {
                details.Add(T("Warnings accepted by the person: {0}", string.Join(", ", review.Acknowledged)));
            }

            events.Add(new TimelineEvent(review.At, TimelineKind.Reviewed, DecidedBy.Person + ": " + review.DecidedBy, ActionText(review.Action), review.InvoiceNumbers, details));
        }

        return events.OrderBy(e => e.At).ToList();
    }

    public static string OutcomeText(Outcome outcome) => outcome switch
    {
        Outcome.AutoMatched => T("Matched automatically."),
        Outcome.HumanReview => T("Sent to a person."),
        _ => T("Not matched on purpose."),
    };

    public static string ActionText(string action) => action switch
    {
        ReviewAction.Confirmed => T("Confirmed the proposal."),
        ReviewAction.MatchedManually => T("Matched manually."),
        ReviewAction.UndoneAi => T("Undid the AI match."),
        _ => T("Rejected: not to be matched."),
    };
}
