namespace PaymentMatching.DataGen.Model;

public enum InvoiceKind
{
    Regular,

    /// <summary>Advance (proforma) invoice – legacy rules treat it specially.</summary>
    Advance,
}
