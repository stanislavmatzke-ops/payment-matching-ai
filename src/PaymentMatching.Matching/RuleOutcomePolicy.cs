using PaymentMatching.Rules;

namespace PaymentMatching.Matching;

/// <summary>What happens with a payment after layer 1.</summary>
public enum NextStep
{
    /// <summary>The decision is final (auto match by rule, review, or no match).</summary>
    Decided,

    /// <summary>The rules could not decide; layer 2 (candidates + AI) takes over.</summary>
    AskAi,
}

public sealed record PolicyResult(NextStep Next, Decision? Decision);

/// <summary>
/// Maps a rule result to the next step. The rules themselves stay as ported; this is where the
/// business decisions Q-02 (partial payments) and Q-07 (payer check) are applied.
/// </summary>
public sealed class RuleOutcomePolicy(MatchingDataset data, MatchingOptions options)
{
    public PolicyResult Apply(BankPayment payment, MatchResult rule)
    {
        var invoices = rule.InvoiceNumber is { Length: > 0 } number ? new[] { number } : [];
        Decision Decide(Outcome outcome, string reason, params string[] flags) =>
            new(payment.Id, outcome, invoices, DecidedBy.Rule(rule.Rule), reason, flags);

        switch (rule.Status)
        {
            case MatchStatus.Skipped:
                return Final(Decide(Outcome.NoMatch, "Bank's own movement (BR-06)."));

            case MatchStatus.Partial when options.PartialPaymentsNeedHuman:
                return Final(Decide(Outcome.HumanReview, PartialReason(payment, rule), Flags.PartialPayment));

            case MatchStatus.Partial:
                return Final(Decide(Outcome.AutoMatched, PartialReason(payment, rule), Flags.PartialPayment));

            case MatchStatus.Paired when rule.Rule.EndsWith("/ADV_OVERPAY", StringComparison.Ordinal) && options.PartialPaymentsNeedHuman:
                return Final(Decide(Outcome.HumanReview, FormattableString.Invariant($"Advance invoice overpaid by {rule.AmountDiff:0.00} CZK."), Flags.Overpayment));

            case MatchStatus.Paired when rule.Rule.StartsWith("FIELD/", StringComparison.Ordinal):
                // VS typed in the VS field and the amount fits: strong evidence even from an unknown account.
                return Final(Decide(Outcome.AutoMatched, "VS and amount match."));

            case MatchStatus.Paired when !options.RequireCustomerPayer || PayerIsCustomer(payment, rule.InvoiceNumber):
                return Final(Decide(Outcome.AutoMatched, "Invoice number in the message, amount and payer match."));

            case MatchStatus.Paired:
                return Final(Decide(Outcome.HumanReview, ThirdPartyReason(payment, rule.InvoiceNumber), Flags.ThirdPartyPayer));

            case MatchStatus.None when rule.Rule == "-/INVALID_AMOUNT":
                return Final(Decide(Outcome.HumanReview, "Amount is zero, negative or unreadable."));

            default:
                return new PolicyResult(NextStep.AskAi, null);
        }
    }

    private static PolicyResult Final(Decision decision) => new(NextStep.Decided, decision);

    private bool PayerIsCustomer(BankPayment payment, string? invoiceNumber) =>
        invoiceNumber is not null
        && data.InvoiceByNumber.TryGetValue(invoiceNumber, out var invoice)
        && data.CustomerOfPayer(payment)?.Id == invoice.CustomerId;

    private string PartialReason(BankPayment payment, MatchResult rule)
    {
        var invoice = data.InvoiceByNumber.GetValueOrDefault(rule.InvoiceNumber ?? "");
        return invoice is null
            ? "Partial payment."
            : FormattableString.Invariant($"Partial payment: {payment.Amount:0.00} of {invoice.Amount:0.00} CZK ({100 * payment.Amount / invoice.Amount:0} %).");
    }

    private string ThirdPartyReason(BankPayment payment, string? invoiceNumber)
    {
        var invoice = data.InvoiceByNumber.GetValueOrDefault(invoiceNumber ?? "");
        var customer = invoice is null ? null : data.CustomerById.GetValueOrDefault(invoice.CustomerId);
        return $"Payer account is not the customer's ({customer?.Name ?? "unknown customer"}); confirm who pays.";
    }
}
