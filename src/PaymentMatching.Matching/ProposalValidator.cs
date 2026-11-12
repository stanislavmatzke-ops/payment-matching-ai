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
        var failures = new List<string>();
        if (injectionSuspected)
        {
            failures.Add(Rejections.InjectionSuspected);
        }

        if (invoiceNumbers.Count == 0)
        {
            failures.Add(Rejections.NoInvoices);
            return new ValidationResult(failures);
        }

        if (invoiceNumbers.Any(n => !candidates.Contains(n)))
        {
            failures.Add(Rejections.NotACandidate);
            return new ValidationResult(failures);
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

        return new ValidationResult(failures);
    }
}
