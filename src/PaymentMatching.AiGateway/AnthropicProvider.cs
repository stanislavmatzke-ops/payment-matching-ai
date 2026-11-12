using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace PaymentMatching.AiGateway;

/// <summary>
/// Claude through the official Anthropic SDK. Structured output (<c>output_config.format</c>)
/// makes the API return JSON that matches the schema; the SDK retries 429/5xx/network errors
/// with backoff. A refusal or a truncated answer is an error, never a result.
/// </summary>
public sealed class AnthropicProvider(AnthropicClient client) : ILlmProvider
{
    public static AnthropicProvider Create(string apiKey, TimeSpan timeout, int maxRetries = 2) =>
        new(new AnthropicClient { ApiKey = apiKey, Timeout = timeout, MaxRetries = maxRetries });

    public async Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.SchemaJson)
            ?? throw new ArgumentException("Schema must be a JSON object.", nameof(request));

        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = request.SystemPrompt,
            Messages = [new UserMessageParam { Content = request.UserContent }],
            OutputConfig = new OutputConfig
            {
                Effort = ParseEffort(request.Effort),
                Format = new JsonOutputFormat { Schema = schema },
            },
        };

        var stopwatch = Stopwatch.StartNew();
        var message = await client.Messages.Create(parameters, cancellationToken);
        stopwatch.Stop();

        string stopReason = message.StopReason?.Raw() ?? "";
        if (stopReason is "refusal" or "max_tokens")
        {
            throw new LlmException($"Model stopped with '{stopReason}'.");
        }

        var json = string.Concat(message.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        var usage = new TokenUsage(
            (int)message.Usage.InputTokens,
            (int)message.Usage.OutputTokens,
            (int)(message.Usage.CacheReadInputTokens ?? 0),
            (int)(message.Usage.CacheCreationInputTokens ?? 0));
        return new LlmResponse(request.Model, json, stopReason, usage, stopwatch.Elapsed);
    }

    private static Effort ParseEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        _ => throw new ArgumentException($"Unknown effort '{effort}'.", nameof(effort)),
    };
}
