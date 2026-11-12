using PaymentMatching.AiGateway;
using PaymentMatching.Matching;

namespace PaymentMatching.Evals;

/// <summary>What answers layer 2 in a configuration.</summary>
public enum Layer2
{
    /// <summary>AI switched off: what the rules cannot decide goes to a person.</summary>
    None,

    /// <summary><see cref="NaiveModelStub"/>: trusts the code's first suggestion, no key needed.</summary>
    Stub,

    /// <summary>A real model (live, recorded or replayed).</summary>
    Model,
}

/// <summary>One way of running the matcher, compared in the eval report.</summary>
public sealed record EvalConfig(string Name, string Description, Layer2 Layer2, GatewayOptions Gateway, MatchingOptions Options)
{
    /// <summary>The configuration the product runs; the strictest gate applies to it.</summary>
    public const string Production = "routing";

    private static readonly MatchingOptions PhaseFour = MatchingOptions.Default with { EscalateOnlyWhenAutoMatchPossible = false };

    public static IReadOnlyList<EvalConfig> All { get; } =
    [
        new("rules", "Ported rules + policy (Q-02, Q-07); AI off, the rest goes to a person", Layer2.None, ModelSettings.Default, MatchingOptions.Default),
        new("stub", "Rules + stub that takes the code's first suggestion (no model)", Layer2.Stub, ModelSettings.Default, MatchingOptions.Default),
        new("small", "Rules + small model only, no escalation", Layer2.Model, ModelSettings.Default with { Large = null }, MatchingOptions.Default),
        new("routing-v1", "Rules + small → large model, escalation on low confidence (phase 4)", Layer2.Model, ModelSettings.Default, PhaseFour),
        new(Production, "Rules + small → large model, escalation only when an automatic match is still possible", Layer2.Model, ModelSettings.Default, MatchingOptions.Default),
    ];

    public static EvalConfig ByName(string name) =>
        All.FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException($"Unknown config '{name}'. Known: {string.Join(", ", All.Select(c => c.Name))}.", nameof(name));

    /// <summary>Same configuration with another automatic-match threshold (escalation unchanged, so recordings still apply).</summary>
    public EvalConfig WithAutoMatchThreshold(double threshold) =>
        this with { Name = FormattableString.Invariant($"{Name}@{threshold:0.00}"), Options = Options with { AutoMatchConfidence = threshold } };
}
