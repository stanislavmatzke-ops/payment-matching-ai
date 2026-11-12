namespace PaymentMatching.AiGateway;

/// <summary>
/// One structured-output request. <see cref="SchemaJson"/> is a JSON schema the provider must
/// enforce on the answer; the caller still validates the answer (a model is never trusted).
/// </summary>
public sealed record LlmRequest(
    string Model,
    string Effort,
    int MaxTokens,
    string SystemPrompt,
    string UserContent,
    string SchemaJson);

public sealed record TokenUsage(int InputTokens, int OutputTokens, int CacheReadTokens = 0, int CacheWriteTokens = 0)
{
    public static TokenUsage None { get; } = new(0, 0);
}

/// <summary>
/// A provider answer. <see cref="Json"/> is the raw text the model produced. <see cref="Cached"/> means
/// the answer was stored earlier for the identical request and no model was called now (ADR 0013).
/// </summary>
public sealed record LlmResponse(string Model, string Json, string StopReason, TokenUsage Usage, TimeSpan Latency)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Cached { get; init; }
}

/// <summary>A model provider behind the gateway (Anthropic today, Azure OpenAI could be another).</summary>
public interface ILlmProvider
{
    Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken);
}

/// <summary>A provider call failed or returned something that is not an answer (refusal, truncated output).</summary>
public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A provider that can answer some requests from stored answers without calling a model (the response
/// cache of the hosted app, ADR 0013). The gateway asks it when the call budget is used up.
/// </summary>
public interface IAnswerCache
{
    Task<bool> ContainsAsync(LlmRequest request, CancellationToken cancellationToken);
}
