using PaymentMatching.AiGateway;

namespace PaymentMatching.Matching;

/// <summary>
/// The models used for matching (ADR 0005). One definition for the CLI, the tests and later the job:
/// every field is part of the record/replay key, so a change here needs new recordings.
/// </summary>
public static class ModelSettings
{
    public static GatewayOptions Default { get; } = new(
        Small: new ModelRoute("claude-haiku-5-5", "low", 2048),
        Large: new ModelRoute("claude-opus-5-5", "medium", 4096));
}
