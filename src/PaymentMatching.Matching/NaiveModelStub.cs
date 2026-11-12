using System.Text.Json;
using PaymentMatching.AiGateway;

namespace PaymentMatching.Matching;

/// <summary>
/// NOT a model. A deterministic stand-in for running the pipeline without an API key (demo, CLI
/// "stub" mode, tests). It trusts the code's hints blindly: the first suggested combination, else
/// the top candidate, always "match" with confidence 0.95 and no flags. Whatever it gets wrong must
/// be caught by validation, which makes it a useful worst-case-ish baseline.
/// </summary>
public sealed class NaiveModelStub : ILlmProvider
{
    public Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var candidates = ReadCandidates(request.UserContent);
        var combination = candidates.GetProperty("suggested_combinations").EnumerateArray().FirstOrDefault();
        var invoices = combination.ValueKind == JsonValueKind.Array
            ? combination.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : candidates.GetProperty("candidates").EnumerateArray().Take(1).Select(c => c.GetProperty("invoice_number").GetString()!).ToArray();

        var answer = JsonSerializer.Serialize(new
        {
            reasoning = "Stub: picked the top candidate suggested by the matching system.",
            decision = invoices.Length > 0 ? AiMatchAnswer.Match : AiMatchAnswer.NoMatch,
            invoice_numbers = invoices,
            confidence = 0.95,
            flags = Array.Empty<string>(),
        });
        return Task.FromResult(new LlmResponse(request.Model, answer, "end_turn", TokenUsage.None, TimeSpan.Zero));
    }

    /// <summary>The JSON inside &lt;candidate_invoices&gt; of a prompt built by <see cref="PromptBuilder"/>.</summary>
    public static JsonElement ReadCandidates(string userContent)
    {
        const string open = "<candidate_invoices>";
        var start = userContent.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = userContent.IndexOf("</candidate_invoices>", StringComparison.Ordinal);
        return JsonDocument.Parse(userContent[start..end]).RootElement.Clone();
    }
}
