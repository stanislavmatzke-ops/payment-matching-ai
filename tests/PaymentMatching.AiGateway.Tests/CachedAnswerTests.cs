using System.Text.Json;

namespace PaymentMatching.AiGateway.Tests;

/// <summary>Answers reused from the response cache (ADR 0013): free, and outside the call budget.</summary>
public class CachedAnswerTests
{
    private const string Small = "claude-haiku-5-5";
    private const string Large = "claude-opus-5-5";

    private static readonly GatewayOptions Options = new(new ModelRoute(Small, "low", 1000), new ModelRoute(Large, "medium", 2000));

    private sealed record Answer(double Confidence);

    /// <summary>Answers "cached" for requests whose user content starts with "cached", like the app's response cache.</summary>
    private sealed class FakeCache(string json) : ILlmProvider, IAnswerCache
    {
        public int ModelCalls { get; private set; }

        public Task<bool> ContainsAsync(LlmRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(request.UserContent.StartsWith("cached", StringComparison.Ordinal));

        public Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            var cached = request.UserContent.StartsWith("cached", StringComparison.Ordinal);
            if (!cached)
            {
                ModelCalls++;
            }

            return Task.FromResult(ScriptedProvider.Response(request.Model, json) with { Cached = cached });
        }
    }

    private static Task<GatewayResult<Answer>> Ask(AiGateway gateway, string user) =>
        gateway.CompleteAsync("system", user, "{}", j => JsonSerializer.Deserialize<Answer>(j, JsonSerializerOptions.Web), a => a.Confidence >= 0.9, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Cached_answer_costs_nothing_and_does_not_use_the_call_budget()
    {
        var gateway = new AiGateway(new FakeCache("""{"confidence":0.95}"""), Options with { MaxCalls = 1 }, PriceTable.Claude);

        var result = await Ask(gateway, "cached 1");

        var call = Assert.Single(result.Calls);
        Assert.True(call.Cached);
        Assert.Equal(0m, call.CostUsd);
        Assert.Equal(1, gateway.CallsLeft);
    }

    [Fact]
    public async Task Over_the_budget_a_cached_answer_is_still_used_but_no_model_is_called()
    {
        var provider = new FakeCache("""{"confidence":0.95}""");
        var gateway = new AiGateway(provider, Options with { MaxCalls = 0 }, PriceTable.Claude);

        var cached = await Ask(gateway, "cached 2");
        var fresh = await Ask(gateway, "new case");

        Assert.Equal(0.95, cached.Value!.Confidence);
        Assert.False(cached.BudgetExhausted);
        Assert.True(fresh.BudgetExhausted);
        Assert.Empty(fresh.Calls);
        Assert.Equal(0, provider.ModelCalls);
    }

    [Fact]
    public void Flag_is_left_out_of_json_when_false_so_recordings_and_audit_records_stay_the_same()
    {
        var plain = JsonSerializer.Serialize(ScriptedProvider.Response(Small, "{}"));
        var cached = JsonSerializer.Serialize(ScriptedProvider.Response(Small, "{}") with { Cached = true });

        Assert.DoesNotContain("Cached", plain, StringComparison.Ordinal);
        Assert.Contains("\"Cached\":true", cached, StringComparison.Ordinal);
    }
}
