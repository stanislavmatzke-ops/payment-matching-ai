using PaymentMatching.AiGateway;
using PaymentMatching.Tests.Shared;
using static PaymentMatching.Matching.Tests.TestData;

namespace PaymentMatching.Matching.Tests;

public class PipelineTelemetryTests
{
    /// <summary>Every tag the libraries may write. A new tag is a deliberate change here (ADR 0015).</summary>
    internal static readonly HashSet<string> AllowedKeys =
    [
        "gen_ai.operation.name", "gen_ai.provider.name", "gen_ai.request.model", "gen_ai.usage.input_tokens",
        "gen_ai.usage.output_tokens", "gen_ai.token.type", "error.type",
        "payments.ai.model", "payments.ai.tier", "payments.ai.outcome", "payments.ai.cost_usd", "payments.ai.cached",
        "payments.ai.latency_ms", "payments.ai.cache_read_tokens", "payments.ai.cache_write_tokens", "payments.ai.escalated",
        "payments.ai.budget_exhausted", "payments.ai.calls",
        "payments.payment.id", "payments.layer", "payments.rule", "payments.outcome", "payments.decided_by", "payments.flags",
        "payments.failures", "payments.candidates", "payments.deferred", "payments.count", "payments.reused",
        "payments.for_ai", "payments.decided",
        "span.name", "span.status",
    ];

    [Fact]
    public async Task Every_decided_payment_is_a_span_with_layer_and_outcome()
    {
        using var capture = TelemetryCapture.Start();

        var report = await new MatchingPipeline(Sample, Gateway(new NaiveModelStub()), new DecisionLog(), MatchingOptions.Default)
            .RunAsync(TestContext.Current.CancellationToken);

        var run = Assert.Single(capture.Named("matching.run"));
        Assert.Equal(Sample.Payments.Count, run.GetTagItem("payments.count"));
        Assert.Equal(report.Records.Count, run.GetTagItem("payments.decided"));

        var payments = capture.Named("matching.payment");
        Assert.Equal(report.Records.Count, payments.Count);
        Assert.All(payments, s => Assert.Equal(run.SpanId, s.ParentSpanId));
        foreach (var record in report.Records)
        {
            var span = Assert.Single(payments, s => Equals(s.GetTagItem("payments.payment.id"), record.PaymentId));
            Assert.Equal(record.Layer, span.GetTagItem("payments.layer"));
            Assert.Equal(record.Decision.Outcome.ToString(), span.GetTagItem("payments.outcome"));
            Assert.Equal(record.Decision.DecidedBy, span.GetTagItem("payments.decided_by"));
        }

        // Model calls hang under the payment they were made for.
        var aiPayments = payments.Where(s => Equals(s.GetTagItem("payments.layer"), AuditRecord.AiLayer)).Select(s => s.SpanId).ToHashSet();
        var requests = capture.Named("ai.complete");
        Assert.NotEmpty(requests);
        Assert.All(requests, r => Assert.Contains(r.ParentSpanId, aiPayments));

        Assert.Equal(report.Records.Count, capture.Sum("payments.matching.decisions"));
        Assert.Equal(
            report.Records.Count(r => r.Decision.Outcome == Outcome.AutoMatched && r.Layer == AuditRecord.RuleLayer),
            capture.Sum("payments.matching.decisions", t => Equals(t["payments.layer"], "rule") && Equals(t["payments.outcome"], "AutoMatched")));
    }

    [Fact]
    public async Task A_payment_left_for_the_next_run_is_marked_deferred()
    {
        var gateway = new AiGateway.AiGateway(new NaiveModelStub(), ModelSettings.Default with { MaxCalls = 0 }, PriceTable.Claude);
        using var capture = TelemetryCapture.Start();

        await new MatchingPipeline(Sample, gateway, new DecisionLog(), MatchingOptions.Default with { DeferAiWhenBudgetExhausted = true })
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains(capture.Named("matching.payment"), s => Equals(s.GetTagItem("payments.deferred"), true));
    }

    /// <summary>
    /// The recorded real-model answers over data/sample, injections included: no text from the bank statement,
    /// no customer data and nothing the model wrote may appear in any span or metric.
    /// </summary>
    [Fact]
    public async Task Telemetry_carries_no_bank_statement_text_customer_data_or_model_output()
    {
        var provider = new RecordReplayProvider(RepoPaths.Combine("evals", "recordings"), RecordingMode.Replay);
        using var capture = TelemetryCapture.Start();

        var report = await new MatchingPipeline(Sample, Gateway(provider), new DecisionLog(), MatchingOptions.Default)
            .RunAsync(TestContext.Current.CancellationToken);

        var tags = capture.AllTags.ToList();
        Assert.Contains(tags, t => t.Key == "gen_ai.usage.input_tokens");
        Assert.Empty(tags.Select(t => t.Key).Where(k => !AllowedKeys.Contains(k)).Distinct());

        var forbidden = Sample.Payments.SelectMany(p => new[] { p.Message, p.CounterpartyName, p.CounterpartyIban, p.VariableSymbol, p.RawLine })
            .Concat(Sample.Customers.SelectMany(c => new[] { c.Name, c.Iban }))
            .Concat(Sample.Invoices.Select(i => i.VariableSymbol))
            .Concat(report.Calls.Select(c => c.RawOutput ?? ""))
            .Concat(report.Records.Select(r => r.Decision.Reason))
            .Where(s => s.Length >= 5)
            .Distinct()
            .ToList();
        Assert.Contains(ExpectedSample.Values, e => e.CaseType == "PromptInjection");
        var leaks = tags.Where(t => forbidden.Any(f => t.Value.Contains(f, StringComparison.Ordinal))).ToList();
        Assert.Empty(leaks);
    }
}
