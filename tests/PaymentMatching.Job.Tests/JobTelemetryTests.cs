using PaymentMatching.AiGateway;
using PaymentMatching.Data;
using PaymentMatching.Testing;
using PaymentMatching.Tests.Shared;

namespace PaymentMatching.Job.Tests;

public class JobTelemetryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_is_one_trace_with_a_span_per_sandbox_under_a_tenant_pseudonym()
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        bed.MoveToDayWithTwoModelCases();
        var sandbox = await bed.SandboxAsync(seed: 7);
        await using (var db = bed.For(sandbox))
        {
            var data = await TenantData.LoadDatasetAsync(db, Ct);
            Assert.NotEmpty(data.Payments);
        }

        using var capture = TelemetryCapture.Start();
        var report = await bed.Job(new CountingModel()).RunAsync(Ct);

        var run = Assert.Single(capture.Named("job.run"));
        Assert.Equal("live", run.GetTagItem("payments.job.mode"));
        Assert.Equal(report.ModelCalls, run.GetTagItem("payments.ai.calls"));
        Assert.Equal((double)report.CostUsd, run.GetTagItem("payments.ai.cost_usd"));

        var span = Assert.Single(capture.Named("job.sandbox"));
        Assert.Equal(run.SpanId, span.ParentSpanId);
        Assert.Equal(Telemetry.TenantHash(sandbox), span.GetTagItem("payments.tenant.hash"));
        Assert.Equal(12, ((string)span.GetTagItem("payments.tenant.hash")!).Length);
        Assert.Equal(report.Sandboxes[0].ModelCalls, span.GetTagItem("payments.ai.calls"));
        Assert.True(report.ModelCalls > 0);
        Assert.Contains(capture.Named("matching.run"), r => r.ParentSpanId == span.SpanId);
        Assert.Equal(report.ModelCalls, capture.Sum("payments.ai.calls", t => Equals(t["payments.ai.cached"], false)));
    }

    [Fact]
    public async Task Telemetry_of_a_run_has_no_tenant_id_and_no_sandbox_data()
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        bed.MoveToDayWithTwoModelCases();
        var sandbox = await bed.SandboxAsync(seed: 9);

        using var capture = TelemetryCapture.Start();
        await bed.Job(new CountingModel()).RunAsync(Ct);

        await using var db = bed.For(sandbox);
        var data = await TenantData.LoadDatasetAsync(db, Ct);
        var forbidden = data.Payments.SelectMany(p => new[] { p.Message, p.CounterpartyName, p.CounterpartyIban, p.VariableSymbol })
            .Concat(data.Customers.SelectMany(c => new[] { c.Name, c.Iban }))
            .Concat([sandbox.ToString(), sandbox.ToString("N"), "tester@example.test"])
            .Where(s => s.Length >= 5)
            .ToList();
        var tags = capture.AllTags.ToList();
        Assert.NotEmpty(tags);
        Assert.DoesNotContain(tags, t => forbidden.Any(f => t.Value.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }
}
