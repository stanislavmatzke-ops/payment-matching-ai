using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>Creates the invoice(s) and the payment for one scenario.</summary>
internal interface ICaseBuilder
{
    CaseType Type { get; }

    PaymentDraft Build(GenerationContext ctx);
}
