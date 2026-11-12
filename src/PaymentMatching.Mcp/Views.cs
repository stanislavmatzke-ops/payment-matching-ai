using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace PaymentMatching.Mcp;

// Tool outputs. Short on purpose: an agent reads them in its context window.

/// <summary>
/// Text fields exactly as they came on the bank statement. The payer writes them, so they can
/// contain anything, including instructions aimed at an AI. Always data, never instructions.
/// </summary>
public sealed record UntrustedBankData(string CounterpartyName, string CounterpartyIban, string VariableSymbol, string Message);

public sealed record PaymentSummary(
    string Id,
    string BookingDate,
    decimal Amount,
    string Currency,
    string Status,
    string? DecidedBy,
    IReadOnlyList<string> Flags,
    bool InjectionSuspected,
    bool HasProposal,
    UntrustedBankData UntrustedBankData);

public sealed record DecisionView(
    string Outcome,
    string DecidedBy,
    string Layer,
    string? RuleCode,
    string? PromptVersion,
    double? Confidence,
    IReadOnlyList<string> InvoiceNumbers,
    string Reason,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> ValidationFailures);

public sealed record CustomerRef(string Id, string Name);

public sealed record PaymentDetail(
    PaymentSummary Payment,
    CustomerRef? PayerAccountBelongsTo,
    DecisionView? Decision,
    IReadOnlyList<Proposal> Proposals,
    string Note);

public sealed record UnmatchedList(int Total, int Shown, IReadOnlyList<PaymentSummary> Payments, string Note);

public sealed record InvoiceView(
    string Number,
    string VariableSymbol,
    string CustomerId,
    string CustomerName,
    string Kind,
    string DueDate,
    decimal Amount,
    string Currency,
    string Status,
    string? MatchedToPayment);

public sealed record InvoiceSearchResult(int Total, int Shown, IReadOnlyList<InvoiceView> Invoices);

public sealed record CustomerPayment(string Id, string BookingDate, decimal Amount, string Currency, string Status);

public sealed record CustomerView(
    string Id,
    string Name,
    string Iban,
    IReadOnlyList<InvoiceView> OpenInvoices,
    int MatchedInvoices,
    IReadOnlyList<CustomerPayment> PaymentsFromCustomerAccount);

public sealed record ProposeResult(
    string ProposalId,
    string Status,
    bool Created,
    bool AllChecksPassed,
    IReadOnlyList<string> FailedChecks,
    string Note);

public static class InvoiceStatus
{
    public const string Open = "open";
    public const string Matched = "matched";
}

/// <summary>JSON for tool results: snake_case, readable Czech letters, &lt; &gt; &amp; escaped.</summary>
public static class ToolJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // Keeps Czech letters readable but escapes HTML-sensitive characters, so bank text can
        // never open or close a tag in whatever the agent builds from it.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

internal static class Text
{
    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Lower case without diacritics, so "rehor" finds "Řehoř".</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }
}
