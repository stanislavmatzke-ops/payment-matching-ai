namespace PaymentMatching.AiGateway;

/// <summary>USD per million tokens.</summary>
public sealed record ModelPrice(decimal InputPerMTok, decimal OutputPerMTok)
{
    /// <summary>Prompt cache reads cost 10 % and cache writes 125 % of the input price.</summary>
    public decimal Cost(TokenUsage usage) =>
        ((usage.InputTokens * InputPerMTok)
         + (usage.OutputTokens * OutputPerMTok)
         + (usage.CacheReadTokens * InputPerMTok * 0.1m)
         + (usage.CacheWriteTokens * InputPerMTok * 1.25m)) / 1_000_000m;
}

/// <summary>
/// List prices of the Claude API (first-party), as published on 2026-11-03.
/// Update together with docs/cost.md when prices change.
/// </summary>
public sealed class PriceTable(IReadOnlyDictionary<string, ModelPrice> prices)
{
    public static PriceTable Claude { get; } = new(new Dictionary<string, ModelPrice>
    {
        ["claude-haiku-5-5"] = new(0.10m, 0.50m),
        ["claude-sonnet-5-5"] = new(2.00m, 10.00m),
        ["claude-opus-5-5"] = new(4.00m, 20.00m),
    });

    public bool Knows(string model) => prices.ContainsKey(model);

    public decimal Cost(string model, TokenUsage usage) =>
        prices.TryGetValue(model, out var price)
            ? price.Cost(usage)
            : throw new ArgumentException($"No price for model '{model}'.", nameof(model));
}
