namespace PaymentMatching.AiGateway.Tests;

public sealed class RecordReplayProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pm-recordings-" + Guid.NewGuid().ToString("N"));

    private static readonly LlmRequest Request = new("claude-haiku-5-5", "low", 1000, "system v1", "payment P0001", """{"type":"object"}""");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Record_saves_the_answer_and_replay_returns_it_without_the_live_provider()
    {
        var live = ScriptedProvider.ByModel(new() { ["claude-haiku-5-5"] = """{"decision":"match"}""" });
        var ct = TestContext.Current.CancellationToken;

        var recorded = await new RecordReplayProvider(_dir, RecordingMode.Record, live).CompleteJsonAsync(Request, ct);
        var replayed = await new RecordReplayProvider(_dir, RecordingMode.Replay).CompleteJsonAsync(Request, ct);

        Assert.Equal(recorded, replayed);
        Assert.Single(live.Requests);
        Assert.Single(Directory.GetFiles(_dir, "*.json"));
    }

    [Fact]
    public async Task Record_mode_reuses_existing_recordings()
    {
        var live = ScriptedProvider.ByModel(new() { ["claude-haiku-5-5"] = "{}" });
        var provider = new RecordReplayProvider(_dir, RecordingMode.Record, live);

        await provider.CompleteJsonAsync(Request, TestContext.Current.CancellationToken);
        await provider.CompleteJsonAsync(Request, TestContext.Current.CancellationToken);

        Assert.Single(live.Requests);
    }

    [Fact]
    public async Task Replay_without_a_recording_fails_instead_of_calling_a_model()
    {
        var provider = new RecordReplayProvider(_dir, RecordingMode.Replay);

        await Assert.ThrowsAsync<RecordingMissingException>(() => provider.CompleteJsonAsync(Request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Key_is_stable_and_changes_with_any_part_of_the_request()
    {
        var key = RecordReplayProvider.Key(Request);

        Assert.Equal(key, RecordReplayProvider.Key(Request with { }));
        Assert.NotEqual(key, RecordReplayProvider.Key(Request with { SystemPrompt = "system v2" }));
        Assert.NotEqual(key, RecordReplayProvider.Key(Request with { Model = "claude-opus-5-5" }));
        Assert.NotEqual(key, RecordReplayProvider.Key(Request with { UserContent = "payment P0002" }));
        Assert.NotEqual(key, RecordReplayProvider.Key(Request with { Effort = "medium" }));
        Assert.NotEqual(key, RecordReplayProvider.Key(Request with { SchemaJson = "{}" }));
    }
}
