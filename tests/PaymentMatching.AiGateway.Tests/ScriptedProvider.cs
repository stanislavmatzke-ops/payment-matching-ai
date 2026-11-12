namespace PaymentMatching.AiGateway.Tests;

/// <summary>Test double: answers per model from a script, counts calls.</summary>
internal sealed class ScriptedProvider(Func<LlmRequest, CancellationToken, Task<LlmResponse>> answer) : ILlmProvider
{
    public List<LlmRequest> Requests { get; } = [];

    public static ScriptedProvider ByModel(Dictionary<string, string> jsonByModel) =>
        new((r, _) => Task.FromResult(Response(r.Model, jsonByModel[r.Model])));

    public static LlmResponse Response(string model, string json) =>
        new(model, json, "end_turn", new TokenUsage(1000, 100), TimeSpan.FromMilliseconds(400));

    public Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return answer(request, cancellationToken);
    }
}
