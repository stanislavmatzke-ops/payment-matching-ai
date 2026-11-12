namespace PaymentMatching.AiGateway;

/// <summary>A model and how to call it.</summary>
public sealed record ModelRoute(string Model, string Effort, int MaxTokens);

/// <param name="Large">Model for escalation; null means the small model answers alone (no escalation).</param>
public sealed record GatewayOptions(ModelRoute Small, ModelRoute? Large)
{
    /// <summary>Wall-clock limit for one provider call (the SDK retries inside it).</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Hard cap on provider calls for one gateway instance (one run). Over the cap no model is called.</summary>
    public int MaxCalls { get; init; } = 500;
}

public enum CallOutcome
{
    /// <summary>Valid and confident answer.</summary>
    Accepted,

    /// <summary>The answer did not parse or did not match the schema.</summary>
    InvalidOutput,

    /// <summary>Valid answer, but the caller's confidence test failed.</summary>
    NotConfident,

    /// <summary>No answer: timeout, transport error, refusal or truncated output.</summary>
    Failed,
}

/// <summary>
/// One provider call, for the audit trail and cost reporting. A <see cref="Cached"/> call reused a stored
/// answer: no model ran, it costs nothing and does not count against <see cref="GatewayOptions.MaxCalls"/>.
/// </summary>
public sealed record UsageRecord(
    string Model,
    string Tier,
    CallOutcome Outcome,
    TokenUsage Usage,
    decimal CostUsd,
    TimeSpan Latency,
    string? RawOutput,
    string? Error)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Cached { get; init; }
}

/// <summary>
/// Result of a routed request. <see cref="Value"/> is null when no model gave a usable answer;
/// the caller then sends the case to a human.
/// </summary>
public sealed record GatewayResult<T>(T? Value, string? Model, IReadOnlyList<UsageRecord> Calls, bool Escalated, bool BudgetExhausted)
    where T : class
{
    public decimal CostUsd => Calls.Sum(c => c.CostUsd);
}

/// <summary>
/// Thin AI gateway: small model first, escalation to the large model when the answer is not
/// usable or not confident, a per-call timeout, a call budget, and token and cost accounting.
/// It knows nothing about payments: the caller supplies the parser and the confidence test.
/// </summary>
public sealed class AiGateway(ILlmProvider provider, GatewayOptions options, PriceTable prices)
{
    private const string TransportError = "transport: ";

    private readonly List<UsageRecord> _calls = [];

    public IReadOnlyList<UsageRecord> Calls => _calls;

    /// <summary>Model calls still allowed; answers reused from the cache are not model calls.</summary>
    public int CallsLeft => Math.Max(0, options.MaxCalls - _calls.Count(c => !c.Cached));

    /// <param name="parse">Strict parser; returns null or throws <see cref="FormatException"/> / <see cref="System.Text.Json.JsonException"/> for an invalid answer.</param>
    /// <param name="isConfident">True when the answer is good enough not to ask the large model.</param>
    public async Task<GatewayResult<T>> CompleteAsync<T>(
        string systemPrompt,
        string userContent,
        string schemaJson,
        Func<string, T?> parse,
        Func<T, bool> isConfident,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var calls = new List<UsageRecord>();
        T? fallback = null;
        string? fallbackModel = null;

        var routes = options.Large is null ? new[] { (options.Small, "small") } : [(options.Small, "small"), (options.Large, "large")];
        foreach (var (route, tier) in routes)
        {
            var request = new LlmRequest(route.Model, route.Effort, route.MaxTokens, systemPrompt, userContent, schemaJson);
            if (CallsLeft == 0 && !await IsCachedAsync(request, cancellationToken))
            {
                return new GatewayResult<T>(fallback, fallbackModel, calls, calls.Count > 0, BudgetExhausted: true);
            }

            var (value, record) = await CallAsync(request, tier, parse, isConfident, cancellationToken);
            calls.Add(record);
            _calls.Add(record);

            if (record.Outcome == CallOutcome.Accepted)
            {
                return new GatewayResult<T>(value, route.Model, calls, Escalated: calls.Count > 1, BudgetExhausted: false);
            }

            if (record.Outcome == CallOutcome.NotConfident)
            {
                // If the large model is not confident either, a human reviews this answer as the proposal.
                fallback = value;
                fallbackModel = route.Model;
            }
            else if (record.Error?.StartsWith(TransportError, StringComparison.Ordinal) == true)
            {
                // The SDK has already retried; asking a second model during an outage only adds cost.
                break;
            }
        }

        return new GatewayResult<T>(fallback, fallbackModel, calls, Escalated: calls.Count > 1, BudgetExhausted: false);
    }

    /// <summary>Over the call budget a stored answer may still be reused: it is not a model call.</summary>
    private async Task<bool> IsCachedAsync(LlmRequest request, CancellationToken cancellationToken) =>
        provider is IAnswerCache cache && await cache.ContainsAsync(request, cancellationToken);

    private async Task<(T? Value, UsageRecord Record)> CallAsync<T>(
        LlmRequest request, string tier, Func<string, T?> parse, Func<T, bool> isConfident, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.CallTimeout);

        LlmResponse response;
        try
        {
            response = await provider.CompleteJsonAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, Failure(request.Model, tier, $"{TransportError}timeout after {options.CallTimeout.TotalSeconds:0} s"));
        }
        catch (LlmException ex)
        {
            return (null, Failure(request.Model, tier, "answer: " + ex.Message));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or Anthropic.Exceptions.AnthropicException)
        {
            return (null, Failure(request.Model, tier, TransportError + ex.Message));
        }

        var cost = response.Cached ? 0m : prices.Cost(request.Model, response.Usage);
        T? value;
        try
        {
            value = parse(response.Json);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            value = null;
        }

        var outcome = value is null ? CallOutcome.InvalidOutput
            : isConfident(value) ? CallOutcome.Accepted
            : CallOutcome.NotConfident;
        return (value, new UsageRecord(request.Model, tier, outcome, response.Usage, cost, response.Latency, response.Json, null) { Cached = response.Cached });
    }

    private static UsageRecord Failure(string model, string tier, string error) =>
        new(model, tier, CallOutcome.Failed, TokenUsage.None, 0m, TimeSpan.Zero, null, error);
}
