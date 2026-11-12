using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PaymentMatching.Matching;

/// <summary>
/// Builds the request for one payment. The system prompt is a versioned file in prompts/
/// (embedded at build time). Bank data goes only into the &lt;untrusted_bank_data&gt; block as JSON;
/// the encoder escapes &lt; and &gt;, so the data can never close or open a tag.
/// </summary>
public sealed class PromptBuilder(MatchingDataset data)
{
    public const string Version = "match-payment.v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // Keeps Czech letters readable but still escapes HTML-sensitive characters (<, >, &, ', ").
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public string SystemPrompt { get; } = LoadPrompt(Version);

    public string UserContent(BankPayment payment, CandidateSet candidates)
    {
        var payer = data.CustomerOfPayer(payment);
        var context = new
        {
            payer_account_belongs_to = payer is null ? null : $"{payer.Name} ({payer.Id})",
            booking_date = payment.BookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var invoices = new
        {
            candidates = candidates.Invoices.Select(c => new
            {
                invoice_number = c.Invoice.Number,
                variable_symbol = c.Invoice.VariableSymbol,
                customer = data.CustomerById.TryGetValue(c.Invoice.CustomerId, out var customer) ? $"{customer.Name} ({customer.Id})" : c.Invoice.CustomerId,
                kind = c.Invoice.Kind,
                amount = c.Invoice.Amount,
                currency = c.Invoice.Currency,
                due_date = c.Invoice.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                signals = c.Signals,
            }),
            suggested_combinations = candidates.Combinations,
        };
        var bank = new
        {
            amount = payment.Amount,
            currency = payment.Currency,
            counterparty_name = payment.CounterpartyName,
            variable_symbol = payment.VariableSymbol,
            message = payment.Message,
        };

        return $"""
            <context>
            {JsonSerializer.Serialize(context, Json)}
            </context>
            <candidate_invoices>
            {JsonSerializer.Serialize(invoices, Json)}
            </candidate_invoices>
            <untrusted_bank_data>
            {JsonSerializer.Serialize(bank, Json)}
            </untrusted_bank_data>
            """;
    }

    private static string LoadPrompt(string version)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"prompts.{version}.md")
            ?? throw new InvalidOperationException($"Prompt {version} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
