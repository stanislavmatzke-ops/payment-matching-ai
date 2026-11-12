using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace PaymentMatching.AiGateway;

/// <summary>
/// Traces and metrics of the payment matching libraries, on the BCL only (ADR 0015): without a listener
/// they cost next to nothing; hosts decide where they go. Only codes, numbers, model names and synthetic
/// ids are ever recorded – never bank statement text, names, prompts, model output or error messages.
/// </summary>
public static class Telemetry
{
    /// <summary>Name of the <see cref="ActivitySource"/> and the <see cref="System.Diagnostics.Metrics.Meter"/>.</summary>
    public const string Name = "PaymentMatching";

    public static ActivitySource Source { get; } = new(Name);

    public static Meter Meter { get; } = new(Name);

    /// <summary>Provider calls, cached answers included (tag <c>payments.ai.cached</c>).</summary>
    public static Counter<long> AiCalls { get; } = Meter.CreateCounter<long>("payments.ai.calls", "{call}", "Model calls and reused cached answers.");

    public static Counter<long> AiTokens { get; } = Meter.CreateCounter<long>("payments.ai.tokens", "{token}", "Tokens billed by the model provider.");

    public static Counter<double> AiCost { get; } = Meter.CreateCounter<double>("payments.ai.cost", "USD", "List-price cost of model calls.");

    public static Histogram<double> AiCallDuration { get; } = Meter.CreateHistogram<double>("payments.ai.call.duration", "s", "Latency of one provider call.");

    public static Counter<long> AiBudgetExhausted { get; } = Meter.CreateCounter<long>("payments.ai.budget_exhausted", "{request}", "Requests that found the call budget used up.");

    public static Counter<long> MatchingDecisions { get; } = Meter.CreateCounter<long>("payments.matching.decisions", "{payment}", "Decided payments by layer and outcome.");

    /// <summary>
    /// A short, stable pseudonym of a tenant id: traces of one sandbox can be followed without the id that
    /// links to a person's sign-in.
    /// </summary>
    public static string TenantHash(Guid tenantId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(tenantId.ToString("N"))))[..12];

    /// <summary>Tag names (OpenTelemetry GenAI semantic conventions where one exists).</summary>
    public static class Tags
    {
        public const string GenAiOperation = "gen_ai.operation.name";
        public const string GenAiProvider = "gen_ai.provider.name";
        public const string GenAiRequestModel = "gen_ai.request.model";
        public const string GenAiInputTokens = "gen_ai.usage.input_tokens";
        public const string GenAiOutputTokens = "gen_ai.usage.output_tokens";
        public const string GenAiTokenType = "gen_ai.token.type";
        public const string ErrorType = "error.type";

        public const string AiModel = "payments.ai.model";
        public const string AiTier = "payments.ai.tier";
        public const string AiOutcome = "payments.ai.outcome";
        public const string AiCostUsd = "payments.ai.cost_usd";
        public const string AiCached = "payments.ai.cached";
        public const string AiLatencyMs = "payments.ai.latency_ms";
        public const string AiCacheReadTokens = "payments.ai.cache_read_tokens";
        public const string AiCacheWriteTokens = "payments.ai.cache_write_tokens";
        public const string AiEscalated = "payments.ai.escalated";
        public const string AiBudgetExhausted = "payments.ai.budget_exhausted";
        public const string AiCalls = "payments.ai.calls";
    }
}
