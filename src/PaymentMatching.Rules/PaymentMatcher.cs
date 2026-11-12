namespace PaymentMatching.Rules;

/// <summary>
/// Deterministic matching rules ported from the legacy PAYMATCH module. Payments are processed
/// in input order; a matched invoice is consumed for the rest of the run (BR-05, BR-19).
/// Rule IDs (BR-xx) refer to docs/business-rules.md.
/// </summary>
public static class PaymentMatcher
{
    private const string HomeCurrency = "CZK";
    private const string BankName = "DEMO BANK";
    private const double Tolerance = 1.00;
    private const double AdvanceOverpaymentLimit = 5000.0;

    public static IReadOnlyList<MatchResult> Match(IReadOnlyList<Invoice> invoices, IReadOnlyList<Payment> payments)
    {
        var lookup = new InvoiceLookup(invoices);
        var paid = new bool[invoices.Count];
        return payments.Select(p => MatchOne(p, invoices, lookup, paid)).ToList();
    }

    private static MatchResult MatchOne(Payment payment, IReadOnlyList<Invoice> invoices, InvoiceLookup lookup, bool[] paid)
    {
        MatchResult NotMatched(string rule, double? diff = null) => new(payment.Id, MatchStatus.None, null, rule, diff);

        // BR-06: bank's own movements
        if (PascalText.UpperAscii(payment.CounterpartyName).Contains(BankName, StringComparison.Ordinal))
        {
            return new MatchResult(payment.Id, MatchStatus.Skipped, null, "-/BANK_OWN", null);
        }

        // BR-07: foreign currency is booked by hand
        if (PascalText.UpperAscii(PascalText.Trim(payment.Currency)) != HomeCurrency)
        {
            return NotMatched("-/CURRENCY");
        }

        // BR-09, BR-10: VS from the field, otherwise from the message
        var vs = VariableSymbol.Normalize(payment.VariableSymbol);
        var fromField = vs.Length > 0;
        var how = "FIELD";
        if (!fromField)
        {
            vs = MessageVsParser.Extract(payment.Message);
            how = "MSG";
        }

        if (vs.Length == 0)
        {
            return NotMatched("-/NO_VS");
        }

        // BR-11, BR-12, BR-13
        var found = lookup.FindExact(vs);
        if (found < 0)
        {
            found = lookup.FindBySubstring(vs);
            if (found >= 0)
            {
                how = "FUZZY";
            }
        }

        if (found < 0)
        {
            return NotMatched($"{how}/NOT_FOUND");
        }

        // BR-14
        if (paid[found])
        {
            return NotMatched($"{how}/DUPLICATE");
        }

        var invoice = invoices[found];
        var diff = payment.Amount - invoice.Amount;

        MatchResult Matched(MatchStatus status, string outcome)
        {
            paid[found] = true; // BR-19
            return new MatchResult(payment.Id, status, invoice.Number, $"{how}/{outcome}", diff);
        }

        // BR-15: tolerance, both directions
        if (Math.Abs(diff) < Tolerance)
        {
            return Matched(MatchStatus.Paired, "EXACT");
        }

        // BR-16: underpayment is a partial match only when the customer typed the VS
        if (diff < 0)
        {
            return fromField ? Matched(MatchStatus.Partial, "PARTIAL") : NotMatched($"{how}/AMOUNT", diff);
        }

        // BR-17, BR-18: overpayment
        return invoice.IsAdvance && diff <= AdvanceOverpaymentLimit
            ? Matched(MatchStatus.Paired, "ADV_OVERPAY")
            : NotMatched($"{how}/OVERPAID", diff);
    }
}
