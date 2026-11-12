using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;
using PaymentMatching.Data;
using PaymentMatching.Matching;

namespace PaymentMatching.Web;

/// <summary>The hidden form field that carries the anti-forgery token.</summary>
public sealed record FormToken(string Name, string Token);

/// <summary>What every signed-in page needs: who is signed in, the token for its forms and how many payments wait.</summary>
public sealed record PageContext(string UserName, FormToken Form)
{
    /// <summary>Payments waiting for the person (badge in the navigation).</summary>
    public int Waiting { get; init; }

    public static PageContext For(HttpContext http, string userName)
    {
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        return new PageContext(userName, new FormToken(tokens.FormFieldName, tokens.RequestToken!));
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

/// <summary>Fixed texts and formats for the pages. Nothing here echoes request input.</summary>
public static class Format
{
    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public static string Amount(decimal amount, string currency) => amount.ToString("N2", CultureInfo.InvariantCulture) + " " + currency;

    public static string Usd(decimal amount) => "$" + amount.ToString(amount is > 0 and < 0.01m ? "0.0000" : "0.00", CultureInfo.InvariantCulture);

    public static string Percent(double? rate) => rate is { } r ? (r * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %" : "–";

    public static string State(string state) => state switch
    {
        PaymentState.Matched => "matched",
        PaymentState.NeedsReview => "waiting for a person",
        PaymentState.NotMatched => "not matched",
        PaymentState.Rejected => "rejected",
        PaymentState.WaitingForAi => "waiting for the AI run",
        _ => state,
    };

    public static string Status(string status) => status switch
    {
        PaymentStatus.AutoMatched => "matched automatically",
        PaymentStatus.HumanReview => "sent to a person",
        PaymentStatus.NoMatch => "not matched on purpose",
        _ => "not processed",
    };

    /// <summary>A flag as a label; the budget flag is set by code, never by a model.</summary>
    public static string Flag(string flag) => flag == Flags.AiBudgetUsedUp ? "AI budget used up" : flag.Replace('_', ' ');

    public static string Budget(BudgetLimit limit) => limit switch
    {
        BudgetLimit.None => "available",
        BudgetLimit.Day => "used up for today (new AI payments go to a person)",
        BudgetLimit.Month => "used up for this month (new AI payments go to a person)",
        _ => "limited for this sandbox today",
    };

    public static string Check(string code) => code switch
    {
        Rejections.NoInvoices => "no invoice chosen.",
        Rejections.NotACandidate => "unknown invoice.",
        Rejections.NotIdentified => "the payment does not name this invoice (no VS or invoice number that fits).",
        Rejections.AlreadyMatched => "an invoice is already matched to another payment.",
        Rejections.MixedCustomers => "the invoices belong to different customers.",
        Rejections.AmountMismatch => "the amount does not fit (partial payment, overpayment or another currency?).",
        Rejections.PayerNotCustomer => "the payer's account is not the invoice customer's (a third party pays?).",
        Rejections.InjectionSuspected => "the payment text looks like instructions to an AI.",
        Rejections.Ambiguous => "another invoice or combination fits just as well.",
        _ => "check failed.",
    };

    public static string Result(ReviewOutcome outcome) => outcome switch
    {
        ReviewOutcome.Done => "Saved.",
        ReviewOutcome.NotFound => "No such payment in your sandbox.",
        ReviewOutcome.AlreadyDecided => "This payment has already been decided.",
        ReviewOutcome.NotActionable => "This payment is not open for a decision.",
        ReviewOutcome.UnknownProposal => "That proposal does not exist (any more).",
        ReviewOutcome.InvalidInvoices => "Pick 1 to 4 different invoices from the list.",
        ReviewOutcome.Blocked => "A check blocks this match. Nothing was saved.",
        ReviewOutcome.NeedsAcknowledgement => "Nothing was saved: tick every warning you checked (the checks may have changed).",
        _ => "",
    };
}
