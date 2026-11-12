using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaymentMatching.Matching;

/// <summary>The model's structured answer. Parsed strictly; it is a proposal until the validator accepts it.</summary>
public sealed record AiMatchAnswer(
    string Reasoning,
    string Decision,
    IReadOnlyList<string> InvoiceNumbers,
    double Confidence,
    IReadOnlyList<string> Flags)
{
    public const string Match = "match";
    public const string NoMatch = "no_match";
    public const string Uncertain = "uncertain";

    private const int MaxReasoningLength = 500;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    /// <summary>JSON schema enforced by the API (structured output). Kept to features every provider supports.</summary>
    public static string Schema { get; } = JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["reasoning"] = new { type = "string" },
            ["decision"] = new { type = "string", @enum = new[] { Match, NoMatch, Uncertain } },
            ["invoice_numbers"] = new { type = "array", items = new { type = "string" } },
            ["confidence"] = new { type = "number" },
            ["flags"] = new { type = "array", items = new { type = "string", @enum = PaymentMatching.Matching.Flags.All } },
        },
        ["required"] = new[] { "reasoning", "decision", "invoice_numbers", "confidence", "flags" },
        ["additionalProperties"] = false,
    });

    /// <summary>Strict parse: unknown or missing fields, unknown values or a confidence outside 0–1 are errors.</summary>
    public static AiMatchAnswer Parse(string json)
    {
        var answer = JsonSerializer.Deserialize<AiMatchAnswer>(json, Options)
            ?? throw new FormatException("Empty answer.");

        if (answer.Decision is not (Match or NoMatch or Uncertain))
        {
            throw new FormatException($"Unknown decision '{answer.Decision}'.");
        }

        if (answer.Confidence is < 0 or > 1 || double.IsNaN(answer.Confidence))
        {
            throw new FormatException("Confidence must be between 0 and 1.");
        }

        if (answer.Flags.Any(f => !PaymentMatching.Matching.Flags.All.Contains(f)))
        {
            throw new FormatException("Unknown flag.");
        }

        return answer with
        {
            Reasoning = answer.Reasoning.Length > MaxReasoningLength ? answer.Reasoning[..MaxReasoningLength] : answer.Reasoning,
            InvoiceNumbers = answer.InvoiceNumbers.Distinct(StringComparer.Ordinal).ToList(),
        };
    }
}
