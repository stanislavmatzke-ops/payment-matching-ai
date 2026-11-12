namespace PaymentMatching.DataGen.Model;

/// <summary>Payment scenarios from SPEC chapter 3. Each generated payment belongs to exactly one.</summary>
public enum CaseType
{
    ExactMatch,
    VsInMessage,
    VsTypo,
    MultiInvoice,
    PartialOrOverpayment,
    ThirdPartyPayer,
    ForeignCurrency,
    Unrelated,
    PromptInjection,
}
