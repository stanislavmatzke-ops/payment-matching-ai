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

    // Hard cases, used by the eval gold set only (GeneratorOptions.EvalCaseMix). On them a matcher
    // that trusts the code's hints and one that reads the payment behave differently.

    /// <summary>The payment fits two invoices (or two combinations) equally well: a person must decide.</summary>
    AmbiguousReference,

    /// <summary>The message names the invoice in a format the matching code does not parse.</summary>
    UnparsedReference,

    /// <summary>An instruction for the matcher without the usual trigger words.</summary>
    HiddenInjection,
}
