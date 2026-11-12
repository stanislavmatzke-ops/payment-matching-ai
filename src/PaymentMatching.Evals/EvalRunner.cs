using PaymentMatching.AiGateway;
using PaymentMatching.Matching;

namespace PaymentMatching.Evals;

/// <summary>One configuration run: the decisions and their score.</summary>
public sealed record EvalRun(EvalConfig Config, IReadOnlyList<AuditRecord> Records, EvalResult Result);

/// <summary>
/// Runs the whole pipeline over a gold set with a fresh in-memory decision log, so every run starts
/// from the same state and runs never influence each other.
/// </summary>
public static class EvalRunner
{
    /// <param name="model">Provider for <see cref="Layer2.Model"/> configurations (replay, record or live).</param>
    public static async Task<EvalRun> RunAsync(GoldSet gold, EvalConfig config, ILlmProvider? model, CancellationToken cancellationToken = default)
    {
        var provider = config.Layer2 switch
        {
            Layer2.None => null,
            Layer2.Stub => new NaiveModelStub(),
            _ => model ?? throw new InvalidOperationException($"Config '{config.Name}' needs a model provider."),
        };
        var gateway = provider is null ? null : new AiGateway.AiGateway(provider, config.Gateway, PriceTable.Claude);
        var report = await new MatchingPipeline(gold.Data, gateway, new DecisionLog(), config.Options).RunAsync(cancellationToken);
        return new EvalRun(config, report.Records, Scorer.Score(config.Name, gold, report.Records));
    }
}
