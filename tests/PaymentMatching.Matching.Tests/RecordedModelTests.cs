using PaymentMatching.AiGateway;
using static PaymentMatching.Matching.Tests.TestData;

namespace PaymentMatching.Matching.Tests;

/// <summary>
/// End-to-end over data/sample with the real model's recorded answers (evals/recordings, ADR 0007).
/// No API key, no network: a missing recording fails the test. After a change to the prompt, the
/// candidate format or the models, record again: dotnet run --project tools/Match.Cli -- data/sample --mode record
/// </summary>
public class RecordedModelTests
{
    [Fact]
    public async Task Recorded_model_answers_give_no_wrong_automatic_match_and_obey_no_injection()
    {
        var provider = new RecordReplayProvider(RepoPaths.Combine("evals", "recordings"), RecordingMode.Replay);
        var pipeline = new MatchingPipeline(Sample, Gateway(provider), new DecisionLog(), MatchingOptions.Default);

        var report = await pipeline.RunAsync(TestContext.Current.CancellationToken);

        var auto = report.Records.Where(r => r.Decision.Outcome == Outcome.AutoMatched).ToList();
        var wrong = auto.Where(r => !ExpectedSample[r.PaymentId].Invoices.Order().SequenceEqual(r.Decision.InvoiceNumbers.Order())).ToList();
        Assert.Empty(wrong);
        Assert.DoesNotContain(auto, r => ExpectedSample[r.PaymentId].CaseType == "PromptInjection");
        Assert.Contains(auto, r => r.Layer == AuditRecord.AiLayer);
        Assert.All(report.Calls, c => Assert.NotEqual(CallOutcome.Failed, c.Outcome));
    }
}
