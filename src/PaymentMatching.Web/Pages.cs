using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;
using PaymentMatching.Data;
using PaymentMatching.Matching;
using static PaymentMatching.Web.Text;

namespace PaymentMatching.Web;

/// <summary>The hidden form field that carries the anti-forgery token.</summary>
public sealed record FormToken(string Name, string Token);

/// <summary>What every signed-in page needs: who is signed in, the token for its forms and how many payments wait.</summary>
public sealed record PageContext(string UserName, FormToken Form)
{
    /// <summary>Payments waiting for the person (badge in the navigation).</summary>
    public int Waiting { get; init; }

    /// <summary>The account is on the <c>Admin:Accounts</c> list: the navigation shows the admin overview.</summary>
    public bool IsAdmin { get; init; }

    public static PageContext For(HttpContext http, string userName)
    {
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        return new PageContext(userName, new FormToken(tokens.FormFieldName, tokens.RequestToken!))
        {
            IsAdmin = http.RequestServices.GetRequiredService<AdminAccounts>().Contains(http.User),
        };
    }
}

/// <summary>
/// Renders a Razor component on the server (static, no interactivity, no scripts). Razor encodes every
/// value it writes, so bank statement text cannot become markup; no component uses MarkupString.
/// </summary>
public static class Html
{
    public static RazorComponentResult Page<TComponent>(object parameters, int status = StatusCodes.Status200OK)
        where TComponent : IComponent =>
        new RazorComponentResult<TComponent>(parameters) { StatusCode = status, ContentType = "text/html; charset=utf-8" };
}

/// <summary>Fixed texts and formats for the pages, in the request's language (<see cref="Text"/>). Nothing here echoes request input.</summary>
public static class Format
{
    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>1,234.56 CZK in English, 1 234,56 CZK in Czech.</summary>
    public static string Amount(decimal amount, string currency) => amount.ToString("N2", NumberCulture) + " " + currency;

    public static string Usd(decimal amount) => "$" + amount.ToString(amount is > 0 and < 0.01m ? "0.0000" : "0.00", NumberCulture);

    public static string Percent(double? rate) => rate is { } r ? (r * 100).ToString("0.0", NumberCulture) + " %" : "–";

    private static CultureInfo NumberCulture => Text.IsCzech ? CultureInfo.GetCultureInfo("cs-CZ") : CultureInfo.InvariantCulture;

    public static string State(string state) => state switch
    {
        PaymentState.Matched => T("matched"),
        PaymentState.NeedsReview => T("waiting for a person"),
        PaymentState.NotMatched => T("not matched"),
        PaymentState.Rejected => T("rejected"),
        PaymentState.WaitingForAi => T("waiting for the AI run"),
        _ => state,
    };

    public static string Status(string status) => status switch
    {
        PaymentStatus.AutoMatched => T("matched automatically"),
        PaymentStatus.HumanReview => T("sent to a person"),
        PaymentStatus.NoMatch => T("not matched on purpose"),
        _ => T("not processed"),
    };

    /// <summary>The filter's decider kinds (<see cref="PaymentQuery.Deciders"/>).</summary>
    public static string Decider(string kind) => kind switch
    {
        DecidedBy.AiKind => T("AI model"),
        DecidedBy.RuleKind => T("a rule"),
        DecidedBy.PersonKind => T("a person"),
        DecidedBy.PolicyKind => T("the policy (no model)"),
        _ => T("nobody yet"),
    };

    /// <summary>A flag as a label; the budget flag is set by code, never by a model.</summary>
    public static string Flag(string flag) => flag == Flags.AiBudgetUsedUp ? T("AI budget used up") : T(flag.Replace('_', ' '));

    public static string Budget(BudgetLimit limit) => limit switch
    {
        BudgetLimit.None => T("available"),
        BudgetLimit.Day => T("used up for today (new AI payments go to a person)"),
        BudgetLimit.Month => T("used up for this month (new AI payments go to a person)"),
        _ => T("limited for this sandbox today"),
    };

    public static string Check(string code) => code switch
    {
        Rejections.NoInvoices => T("no invoice chosen."),
        Rejections.NotACandidate => T("unknown invoice."),
        Rejections.NotIdentified => T("the payment does not name this invoice (no VS or invoice number that fits)."),
        Rejections.AlreadyMatched => T("an invoice is already matched to another payment."),
        Rejections.MixedCustomers => T("the invoices belong to different customers."),
        Rejections.AmountMismatch => T("the amount does not fit (partial payment, overpayment or another currency?)."),
        Rejections.PayerNotCustomer => T("the payer's account is not the invoice customer's (a third party pays?)."),
        Rejections.InjectionSuspected => T("the payment text looks like instructions to an AI."),
        Rejections.Ambiguous => T("another invoice or combination fits just as well."),
        _ => T("check failed."),
    };

    public static string Result(ReviewOutcome outcome) => outcome switch
    {
        ReviewOutcome.Done => T("Saved."),
        ReviewOutcome.NotFound => T("No such payment in your sandbox."),
        ReviewOutcome.AlreadyDecided => T("This payment has already been decided."),
        ReviewOutcome.NotActionable => T("This payment is not open for a decision."),
        ReviewOutcome.UnknownProposal => T("That proposal does not exist (any more)."),
        ReviewOutcome.InvalidInvoices => T("Pick 1 to 4 different invoices from the list."),
        ReviewOutcome.Blocked => T("A check blocks this match. Nothing was saved."),
        ReviewOutcome.NeedsAcknowledgement => T("Nothing was saved: tick every warning you checked (the checks may have changed)."),
        _ => "",
    };

    /// <summary>
    /// What a rule code means, for people (ADR 0004 codes: where the reference was found / what happened).
    /// The code itself stays visible next to it for the audit trail.
    /// </summary>
    public static string Rule(string? code) => RuleTexts.TryGetValue(code ?? "", out var text) ? T(text) : code ?? "";

    /// <summary>Every rule code with its English explanation (the legend page lists them in this order).</summary>
    public static readonly IReadOnlyList<(string Code, string Text)> RuleLegend =
    [
        ("FIELD/EXACT", "Exact match: the variable symbol in the VS field names the invoice and the amount fits."),
        ("MSG/EXACT", "Exact match: the invoice number or VS was found in the message and the amount fits."),
        ("FIELD/PARTIAL", "Partial payment: the VS names the invoice, but less was paid than invoiced."),
        ("FIELD/ADV_OVERPAY", "Advance invoice paid with more than invoiced."),
        ("FIELD/OVERPAID", "More was paid than invoiced (not an advance invoice)."),
        ("MSG/OVERPAID", "Invoice found in the message, but more was paid than invoiced."),
        ("MSG/ADV_OVERPAY", "Advance invoice found in the message, paid with more than invoiced."),
        ("MSG/AMOUNT", "Invoice found in the message, but less was paid than invoiced (a partial payment counts only with the VS in the VS field)."),
        ("FIELD/NOT_FOUND", "The VS field names no existing invoice."),
        ("MSG/NOT_FOUND", "The message names an invoice that does not exist."),
        ("FIELD/DUPLICATE", "The invoice named in the VS field is already paid."),
        ("MSG/DUPLICATE", "The invoice named in the message is already paid."),
        ("-/NO_VS", "No variable symbol and no invoice number: the rules cannot tell which invoice."),
        ("-/CURRENCY", "A currency the rules do not handle."),
        ("-/INVALID_AMOUNT", "The amount is missing or not valid."),
        ("-/BANK_OWN", "The bank's own entry (fees, interest): not a customer payment."),
    ];

    private static readonly Dictionary<string, string> RuleTexts = RuleLegend.ToDictionary(r => r.Code, r => r.Text, StringComparer.Ordinal);
}
