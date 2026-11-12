namespace PaymentMatching.Rules;

/// <summary>Finds an invoice by normalized VS. The first invoice in input order wins (BR-11).</summary>
/// <remarks>
/// Change F1: the legacy substring ("fuzzy") lookup (BR-12) is gone. It matched wrong invoices
/// (docs/business-rules.md, R-02). A VS that is not found exactly is left for the next layer.
/// Change F2: invoices with a zero or negative amount are never matched.
/// </remarks>
internal sealed class InvoiceLookup(IReadOnlyList<Invoice> invoices)
{
    /// <returns>Index of the invoice, or -1.</returns>
    public int FindExact(string vs)
    {
        for (var i = 0; i < invoices.Count; i++)
        {
            if (invoices[i].VariableSymbol == vs && invoices[i].Amount > 0)
            {
                return i;
            }
        }

        return -1;
    }
}
