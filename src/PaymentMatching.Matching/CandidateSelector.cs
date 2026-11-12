namespace PaymentMatching.Matching;

/// <summary>Why an invoice is a candidate. The model sees these hints; the validator re-checks everything.</summary>
public static class Signals
{
    public const string VsExact = "vs_exact";
    public const string VsOneTypo = "vs_one_typo";
    public const string NumberInMessage = "number_in_message";
    public const string AmountExact = "amount_exact";
    public const string AmountFx = "amount_in_eur_rate_band";
    public const string PayerIsCustomer = "payer_is_customer";
    public const string InCombination = "in_combination";
}

public sealed record Candidate(InvoiceRecord Invoice, IReadOnlyList<string> Signals, int Score);

/// <summary>Candidates for one payment: at most N invoices, plus combinations of invoices whose sum fits.</summary>
public sealed record CandidateSet(IReadOnlyList<Candidate> Invoices, IReadOnlyList<IReadOnlyList<string>> Combinations)
{
    public bool IsEmpty => Invoices.Count == 0;

    public bool Contains(string invoiceNumber) => Invoices.Any(c => c.Invoice.Number == invoiceNumber);
}

/// <summary>
/// Code, not the model, chooses what the model may look at: open invoices related to the payment by
/// VS (exact or one typo), an invoice number in the message, the amount, or the payer account.
/// The model never sees the whole invoice database.
/// </summary>
public sealed class CandidateSelector(MatchingDataset data, MatchingOptions options)
{
    private const int MaxCombinationSize = 4;
    private const int MaxInvoicesPerCustomerForCombinations = 20;

    public CandidateSet Select(BankPayment payment, IReadOnlySet<string> unavailable)
    {
        var open = data.Invoices.Where(i => !unavailable.Contains(i.Number) && i.Amount > 0).ToList();
        var payer = data.CustomerOfPayer(payment);
        var vs = PaymentMatching.Rules.VariableSymbol.Normalize(payment.VariableSymbol);
        var referenced = InvoiceReferences.InMessage(payment.Message);
        var messageVs = referenced.Select(r => r.Replace("-", "", StringComparison.Ordinal)).ToHashSet();

        var combinations = Combinations(payment, open, payer, referenced);
        var inCombination = combinations.SelectMany(c => c).ToHashSet();

        var candidates = new List<Candidate>();
        foreach (var invoice in open)
        {
            var signals = new List<string>();
            var score = 0;
            void Add(string signal, int points)
            {
                signals.Add(signal);
                score += points;
            }

            if (vs.Length > 0 && vs == invoice.VariableSymbol)
            {
                Add(Signals.VsExact, 100);
            }
            else if (vs.Length >= 8 && InvoiceReferences.IsOneTypoApart(vs, invoice.VariableSymbol))
            {
                Add(Signals.VsOneTypo, 60);
            }

            if (referenced.Contains(InvoiceReferences.Canonical(invoice.Number)) || messageVs.Contains(invoice.VariableSymbol))
            {
                Add(Signals.NumberInMessage, 80);
            }

            if (inCombination.Contains(invoice.Number))
            {
                Add(Signals.InCombination, 70);
            }

            if (AmountFits(payment, [invoice]))
            {
                Add(IsEur(payment) ? Signals.AmountFx : Signals.AmountExact, 50);
            }

            var payerMatches = payer?.Id == invoice.CustomerId;
            if (payerMatches)
            {
                Add(Signals.PayerIsCustomer, 20);
            }

            // An EUR amount fits ~10 % of all invoices, and "payer is customer" alone fits all of the
            // customer's invoices: on their own these are not evidence.
            var evidence = signals.Any(s => s is Signals.VsExact or Signals.VsOneTypo or Signals.NumberInMessage or Signals.InCombination or Signals.AmountExact)
                || (payerMatches && signals.Contains(Signals.AmountFx));
            if (evidence)
            {
                candidates.Add(new Candidate(invoice, signals, score));
            }
        }

        var top = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Invoice.Number, StringComparer.Ordinal)
            .Take(options.MaxCandidates)
            .ToList();
        var kept = top.Select(c => c.Invoice.Number).ToHashSet();
        return new CandidateSet(top, combinations.Where(c => c.All(kept.Contains)).ToList());
    }

    /// <summary>Sum of the invoices equals the payment: CZK within the tolerance, EUR within the rate band.</summary>
    public bool AmountFits(BankPayment payment, IReadOnlyCollection<InvoiceRecord> invoices)
    {
        var sum = invoices.Sum(i => i.Amount);
        if (payment.Amount <= 0 || invoices.Any(i => !IsCzk(i.Currency)))
        {
            return false;
        }

        if (IsCzk(payment.Currency))
        {
            return Math.Abs(sum - payment.Amount) < options.Tolerance;
        }

        // The rate band is ~10 % wide, so an EUR amount cannot tell several invoices apart: one invoice only.
        if (IsEur(payment) && invoices.Count == 1)
        {
            var rate = sum / payment.Amount;
            return rate >= options.MinEurRate && rate <= options.MaxEurRate;
        }

        return false;
    }

    private static bool IsCzk(string currency) => string.Equals(currency.Trim(), "CZK", StringComparison.OrdinalIgnoreCase);

    private static bool IsEur(BankPayment payment) => string.Equals(payment.Currency.Trim(), "EUR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 2–4 open invoices of one customer whose sum fits the payment. Customers considered: the payer,
    /// and the customers of invoices referenced by VS or message (a multi-invoice payment usually names one).
    /// </summary>
    private List<IReadOnlyList<string>> Combinations(BankPayment payment, List<InvoiceRecord> open, Customer? payer, IReadOnlySet<string> referenced)
    {
        var vs = PaymentMatching.Rules.VariableSymbol.Normalize(payment.VariableSymbol);
        var customerIds = open
            .Where(i => i.VariableSymbol == vs || referenced.Contains(InvoiceReferences.Canonical(i.Number)))
            .Select(i => i.CustomerId)
            .Append(payer?.Id)
            .OfType<string>()
            .Distinct();

        var result = new List<IReadOnlyList<string>>();
        foreach (var customerId in customerIds)
        {
            var own = open.Where(i => i.CustomerId == customerId)
                .OrderBy(i => i.DueDate)
                .Take(MaxInvoicesPerCustomerForCombinations)
                .ToList();
            for (var size = 2; size <= MaxCombinationSize; size++)
            {
                foreach (var combo in Subsets(own, size))
                {
                    if (AmountFits(payment, combo))
                    {
                        result.Add(combo.Select(i => i.Number).ToList());
                    }
                }
            }
        }

        return result;
    }

    private static IEnumerable<List<InvoiceRecord>> Subsets(List<InvoiceRecord> items, int size, int start = 0)
    {
        if (size == 0)
        {
            yield return [];
            yield break;
        }

        for (var i = start; i <= items.Count - size; i++)
        {
            foreach (var rest in Subsets(items, size - 1, i + 1))
            {
                rest.Insert(0, items[i]);
                yield return rest;
            }
        }
    }
}
