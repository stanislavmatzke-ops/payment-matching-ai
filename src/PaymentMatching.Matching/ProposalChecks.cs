namespace PaymentMatching.Matching;

/// <summary>
/// Validation of a match that did not come from the pipeline's own candidate selection: an agent's
/// proposal (MCP) or a person's choice in the UI. It is the pipeline's <see cref="ProposalValidator"/>
/// over the code's candidates plus the chosen invoices. An invoice code would not have offered carries
/// no signals, so a choice based on the amount alone fails "the payment must name the invoice".
/// </summary>
public sealed class ProposalChecks(MatchingDataset data, MatchingOptions options)
{
    public const int MaxInvoices = 4;

    /// <summary>
    /// Failures a person can never override: the invoices do not exist, are already matched, belong to
    /// different customers, or there are none or too many. The other failures are warnings: they are
    /// exactly the cases a person is asked to judge (partial payment, third-party payer, unreadable
    /// reference, a tie), so the person sees them and must acknowledge each one.
    /// </summary>
    public static IReadOnlySet<string> Blocking { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Rejections.NoInvoices,
        Rejections.NotACandidate,
        Rejections.AlreadyMatched,
        Rejections.MixedCustomers,
    };

    private readonly CandidateSelector _selector = new(data, options);

    /// <summary>Validation failures of matching <paramref name="payment"/> to <paramref name="invoiceNumbers"/> (empty = all checks passed).</summary>
    /// <param name="unavailable">Invoices already matched to another payment.</param>
    public IReadOnlyList<string> Check(BankPayment payment, IReadOnlyList<string> invoiceNumbers, IReadOnlySet<string> unavailable, bool injectionSuspected)
    {
        if (invoiceNumbers.Count is 0 or > MaxInvoices || invoiceNumbers.Any(n => !data.InvoiceByNumber.ContainsKey(n)))
        {
            return [invoiceNumbers.Count == 0 ? Rejections.NoInvoices : Rejections.NotACandidate];
        }

        var selected = _selector.Select(payment, unavailable);
        var extra = invoiceNumbers.Where(n => !selected.Contains(n)).Select(n => new Candidate(data.InvoiceByNumber[n], [], 0));
        var candidates = selected with { Invoices = [.. selected.Invoices, .. extra] };
        var validator = new ProposalValidator(data, _selector, options);
        return validator.Validate(payment, invoiceNumbers, candidates, unavailable, injectionSuspected).Failures;
    }

    /// <summary>The payment's text looks like an instruction, or the pipeline flagged it as one.</summary>
    public static bool InjectionSuspected(BankPayment payment, AuditRecord? decision) =>
        InjectionDetector.LooksLikeInjection($"{payment.Message} {payment.CounterpartyName} {payment.VariableSymbol}")
        || decision?.Decision.Flags.Contains(Flags.PromptInjection) == true;
}
