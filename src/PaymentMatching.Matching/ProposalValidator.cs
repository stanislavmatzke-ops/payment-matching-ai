namespace PaymentMatching.Matching;

/// <summary>Why a model proposal was rejected. Stored in the audit trail.</summary>
public static class Rejections
{
    public const string NoInvoices = "NO_INVOICES";
    public const string NotACandidate = "NOT_A_CANDIDATE";
    public const string NotIdentified = "NOT_IDENTIFIED_BY_PAYMENT";
    public const string AlreadyMatched = "INVOICE_ALREADY_MATCHED";
    public const string MixedCustomers = "MIXED_CUSTOMERS";
    public const string AmountMismatch = "AMOUNT_MISMATCH";
    public const string PayerNotCustomer = "PAYER_NOT_CUSTOMER";
    public const string InjectionSuspected = "PROMPT_INJECTION_SUSPECTED";
    public const string Ambiguous = "AMBIGUOUS";
}

public sealed record ValidationResult(IReadOnlyList<string> Failures)
{
    public bool IsValid => Failures.Count == 0;
}

/// <summary>
/// Code checks every model proposal before anything is matched. The model may only pick from the
/// candidates; the amount, the customer, the payer and the "still open" state are re-checked here,
/// whatever the model says about them.
/// </summary>
public sealed class ProposalValidator(MatchingDataset data, CandidateSelector selector, MatchingOptions options)
{
    public ValidationResult Validate(BankPayment payment, IReadOnlyList<string> invoiceNumbers, CandidateSet candidates, IReadOnlySet<string> unavailable, bool injectionSuspected)
    {
        var failures = CheckProposal(payment, invoiceNumbers, candidates, unavailable, injectionSuspected);

        // A proposal is only as good as its uniqueness: when another candidate set passes the same
        // checks (two invoices with the same amount and the same VS typo, two combinations with the
        // same total), the payment does not tell which one it pays. A person decides.
        if (failures.Count == 0 && ProposalSets(candidates).Any(set => !SameSet(set, invoiceNumbers) && CheckProposal(payment, set, candidates, unavailable, false).Count == 0))
        {
            failures.Add(Rejections.Ambiguous);
        }

        return new ValidationResult(failures);
    }

    /// <summary>True when some candidate invoice or suggested combination passes validation (a tie passes none).</summary>
    public bool AnyValidProposal(BankPayment payment, CandidateSet candidates, IReadOnlySet<string> unavailable) =>
        ProposalSets(candidates).Any(set => Validate(payment, set, candidates, unavailable, injectionSuspected: false).IsValid);

    private static IEnumerable<IReadOnlyList<string>> ProposalSets(CandidateSet candidates) =>
        candidates.Invoices.Select(c => (IReadOnlyList<string>)[c.Invoice.Number]).Concat(candidates.Combinations);

    private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.Count == b.Count && a.All(b.Contains);

    private List<string> CheckProposal(BankPayment payment, IReadOnlyList<string> invoiceNumbers, CandidateSet candidates, IReadOnlySet<string> unavailable, bool injectionSuspected)
    {
        var failures = new List<string>();
        if (injectionSuspected)
        {
            failures.Add(Rejections.InjectionSuspected);
        }

        if (invoiceNumbers.Count == 0)
        {
            failures.Add(Rejections.NoInvoices);
            return failures;
        }

        if (invoiceNumbers.Any(n => !candidates.Contains(n)))
        {
            failures.Add(Rejections.NotACandidate);
            return failures;
        }

        // The payer's own identifiers win over amounts: the proposal must include every invoice the
        // payment names exactly (VS field or number in the message), and at least one invoice it names
        // at all (exact or with one typo). An amount that merely fits identifies nothing.
        var named = candidates.Invoices.Where(c => c.Signals.Contains(Signals.VsExact) || c.Signals.Contains(Signals.NumberInMessage)).Select(c => c.Invoice.Number);
        var identified = candidates.Invoices.Where(c => c.Signals.Contains(Signals.VsOneTypo)).Select(c => c.Invoice.Number).Concat(named).ToHashSet();
        if (named.Any(n => !invoiceNumbers.Contains(n)) || !invoiceNumbers.Any(identified.Contains))
        {
            failures.Add(Rejections.NotIdentified);
        }

        var invoices = invoiceNumbers.Select(n => data.InvoiceByNumber[n]).ToList();
        if (invoices.Any(i => unavailable.Contains(i.Number)))
        {
            failures.Add(Rejections.AlreadyMatched);
        }

        if (invoices.Select(i => i.CustomerId).Distinct().Count() > 1)
        {
            failures.Add(Rejections.MixedCustomers);
        }

        if (!selector.AmountFits(payment, invoices))
        {
            failures.Add(Rejections.AmountMismatch);
        }

        if (options.RequireCustomerPayer && data.CustomerOfPayer(payment)?.Id != invoices[0].CustomerId)
        {
            failures.Add(Rejections.PayerNotCustomer);
        }

        return failures;
    }
}
