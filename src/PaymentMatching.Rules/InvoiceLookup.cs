namespace PaymentMatching.Rules;

/// <summary>Finds an invoice by normalized VS. The first invoice in input order wins (BR-11).</summary>
internal sealed class InvoiceLookup(IReadOnlyList<Invoice> invoices)
{
    private const int FuzzyMinLength = 8;

    /// <returns>Index of the invoice, or -1.</returns>
    public int FindExact(string vs)
    {
        for (var i = 0; i < invoices.Count; i++)
        {
            if (invoices[i].VariableSymbol == vs)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// BR-12: first invoice whose VS contains <paramref name="vs"/> anywhere. Kept from legacy for
    /// the faithful port; it matches wrong invoices (see docs/business-rules.md, R-02).
    /// </summary>
    public int FindBySubstring(string vs)
    {
        if (vs.Length < FuzzyMinLength)
        {
            return -1;
        }

        for (var i = 0; i < invoices.Count; i++)
        {
            if (invoices[i].VariableSymbol.Contains(vs, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
