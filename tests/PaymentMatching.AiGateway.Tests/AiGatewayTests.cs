using System.Text.Json;

namespace PaymentMatching.AiGateway.Tests;

public class AiGatewayTests
{
    private const string Small = "claude-haiku-5-5";
    private const string Large = "claude-opus-5-5";

    private static readonly GatewayOptions Options = new(new ModelRoute(Small, "low", 1000), new ModelRoute(Large, "medium", 2000));

    private sealed record Answer(double Confidence);

    private static Answer? Parse(string json) => JsonSerializer.Deserialize<Answer>(json, JsonSerializerOptions.Web);

    private static bool Confident(Answer a) => a.Confidence >= 0.9;

    private static Task<GatewayResult<Answer>> Ask(AiGateway gateway) =>
        gateway.CompleteAsync("system", "user", "{}", Parse, Confident, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Confident_small_model_answer_is_used_without_escalation()
    {
        var provider = ScriptedProvider.ByModel(new() { [Small] = """{"confidence":0.95}""" });

        var result = await Ask(new AiGateway(provider, Options, PriceTable.Claude));

        Assert.Equal(0.95, result.Value!.Confidence);
        Assert.Equal(Small, result.Model);
        Assert.False(result.Escalated);
        Assert.Single(provider.Requests);
        Assert.Equal("low", provider.Requests[0].Effort);
    }

    [Theory]
    [InlineData("""{"confidence":0.5}""")]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task Unsure_or_invalid_small_answer_escalates_to_the_large_model(string smallAnswer)
    {
        var provider = ScriptedProvider.ByModel(new() { [Small] = smallAnswer, [Large] = """{"confidence":0.97}""" });
        var gateway = new AiGateway(provider, Options, PriceTable.Claude);

        var result = await Ask(gateway);

        Assert.Equal(Large, result.Model);
        Assert.True(result.Escalated);
        Assert.Equal([Small, Large], provider.Requests.Select(r => r.Model));
        Assert.Equal(2, gateway.Calls.Count);
    }

    [Fact]
    public async Task When_neither_model_is_confident_the_large_answer_is_returned_as_a_proposal()
    {
        var provider = ScriptedProvider.ByModel(new() { [Small] = """{"confidence":0.5}""", [Large] = """{"confidence":0.6}""" });

        var result = await Ask(new AiGateway(provider, Options, PriceTable.Claude));

        Assert.Equal(0.6, result.Value!.Confidence);
        Assert.Equal(Large, result.Model);
        Assert.All(result.Calls, c => Assert.Equal(CallOutcome.NotConfident, c.Outcome));
    }

    [Fact]
    public async Task Refusal_on_the_small_model_escalates()
    {
        var provider = new ScriptedProvider((r, _) => r.Model == Small
            ? throw new LlmException("Model stopped with 'refusal'.")
            : Task.FromResult(ScriptedProvider.Response(r.Model, """{"confidence":0.99}""")));

        var result = await Ask(new AiGateway(provider, Options, PriceTable.Claude));

        Assert.Equal(Large, result.Model);
        Assert.Equal(CallOutcome.Failed, result.Calls[0].Outcome);
        Assert.StartsWith("answer:", result.Calls[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_is_a_failure_and_does_not_call_the_second_model()
    {
        var provider = new ScriptedProvider(async (r, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return ScriptedProvider.Response(r.Model, "{}");
        });
        var options = Options with { CallTimeout = TimeSpan.FromMilliseconds(50) };

        var result = await Ask(new AiGateway(provider, options, PriceTable.Claude));

        Assert.Null(result.Value);
        var call = Assert.Single(result.Calls);
        Assert.Equal(CallOutcome.Failed, call.Outcome);
        Assert.Contains("timeout", call.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Call_budget_stops_calling_the_provider()
    {
        var provider = ScriptedProvider.ByModel(new() { [Small] = """{"confidence":0.99}""" });
        var gateway = new AiGateway(provider, Options with { MaxCalls = 1 }, PriceTable.Claude);

        await Ask(gateway);
        var second = await Ask(gateway);

        Assert.True(second.BudgetExhausted);
        Assert.Null(second.Value);
        Assert.Single(provider.Requests);
        Assert.Equal(0, gateway.CallsLeft);
    }

    [Fact]
    public async Task Every_call_records_tokens_and_cost()
    {
        var provider = ScriptedProvider.ByModel(new() { [Small] = """{"confidence":0.1}""", [Large] = """{"confidence":0.99}""" });

        var result = await Ask(new AiGateway(provider, Options, PriceTable.Claude));

        // 1 000 input + 100 output tokens on each model.
        Assert.Equal(0.00015m, result.Calls[0].CostUsd);
        Assert.Equal(0.006m, result.Calls[1].CostUsd);
        Assert.Equal(0.00615m, result.CostUsd);
        Assert.All(result.Calls, c => Assert.Equal(new TokenUsage(1000, 100), c.Usage));
    }

    [Fact]
    public void Price_table_rejects_unknown_models()
    {
        Assert.Equal(0.60m, PriceTable.Claude.Cost(Small, new TokenUsage(1_000_000, 1_000_000)));
        Assert.Throws<ArgumentException>(() => PriceTable.Claude.Cost("gpt-unknown", TokenUsage.None));
    }
}
