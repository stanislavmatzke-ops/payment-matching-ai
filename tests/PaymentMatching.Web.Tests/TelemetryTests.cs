using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using PaymentMatching.Observability;
using PaymentMatching.Testing;

namespace PaymentMatching.Web.Tests;

/// <summary>
/// The OpenTelemetry pipeline of the hosts (ADR 0015). The instrumentation listens process-wide and sees other
/// tests' requests too, so assertions look for this test's unique paths and markers, never for counts.
/// </summary>
public class TelemetryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Without_an_exporter_setting_the_sdk_is_not_started()
    {
        var services = new ServiceCollection().AddPaymentsTelemetry("test", Config());
        using var provider = services.BuildServiceProvider();

        Assert.False(TelemetrySettings.From(Config()).Enabled);
        Assert.Null(provider.GetService<TracerProvider>());
        using var console = TelemetrySetup.StartConsole("test", Config());
    }

    [Fact]
    public void Exporters_are_chosen_by_configuration_only()
    {
        var otlp = TelemetrySettings.From(Config(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317")));
        var azure = TelemetrySettings.From(Config(("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=00000000-0000-0000-0000-000000000000")));

        Assert.Equal(new Uri("http://localhost:4317"), otlp.OtlpEndpoint);
        Assert.True(otlp.Enabled);
        Assert.Null(otlp.AzureMonitor);
        Assert.True(azure.Enabled);
        Assert.Null(azure.OtlpEndpoint);
    }

    [Fact]
    public void Redaction_removes_query_strings_user_agents_sql_and_foreign_error_messages()
    {
        using var source = new ActivitySource("Foreign.Library");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("call")!;
        activity.SetTag("url.query", "name=alice")
            .SetTag("url.full", "https://idp.example.test/callback?code=secret&state=x")
            .SetTag("user_agent.original", "Mozilla/5.0")
            .SetTag("db.query.text", "select 'Jan Novak'")
            .SetTag("url.path", "/dev-login")
            .SetStatus(ActivityStatusCode.Error, "duplicate key (number)=(2026-0001)");

        new RedactionProcessor().OnEnd(activity);

        Assert.Null(activity.GetTagItem("url.query"));
        Assert.Null(activity.GetTagItem("user_agent.original"));
        Assert.Null(activity.GetTagItem("db.query.text"));
        Assert.Equal("https://idp.example.test/callback", activity.GetTagItem("url.full"));
        Assert.Equal("/dev-login", activity.GetTagItem("url.path"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Null(activity.StatusDescription);
    }

    [Fact]
    public async Task Web_requests_are_traced_by_route_without_query_names_or_user_agent()
    {
        var spans = new List<Activity>();
        await using var app = new WebApp(
            await postgres.NewDatabaseAsync(Ct),
            settings: new Dictionary<string, string?> { ["Telemetry:Enabled"] = "true" },
            services: s => s.ConfigureOpenTelemetryTracerProvider(b => b.AddInMemoryExporter(spans)));
        var client = await app.SignedInAsAsync("zuzanatelemetry");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TelemetryProbe/1.0");
        var me = await WebApp.MeAsync(client);
        await client.GetAsync("/app.css", Ct);
        await client.GetAsync("/api/payments/PXPROBE?state=needs_review", Ct);

        List<Activity> Ours()
        {
            lock (spans)
            {
                return spans.Where(s => s.Kind == ActivityKind.Server).ToList();
            }
        }

        // The test server may end a request's span just after the client has the response.
        for (var i = 0; i < 50 && !Ours().Any(s => Equals(s.GetTagItem("url.path"), "/api/payments/PXPROBE")); i++)
        {
            await Task.Delay(20, Ct);
        }

        var server = Ours();
        // Other tests run in parallel and the instrumentation listens process-wide: assert on presence only.
        Assert.Contains(server, s => Equals(s.GetTagItem("url.path"), "/dev-login") && Equals(s.GetTagItem("http.route"), "/dev-login"));
        Assert.Contains(server, s => Equals(s.GetTagItem("http.route"), "/api/me"));
        Assert.Contains(server, s => Equals(s.GetTagItem("url.path"), "/api/payments/PXPROBE") && Equals(s.GetTagItem("http.route"), "/api/payments/{id}"));
        Assert.DoesNotContain(server, s => Equals(s.GetTagItem("url.path"), "/app.css"));

        var values = server.SelectMany(s => s.TagObjects).Select(t => (t.Key, Value: Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "")).ToList();
        Assert.DoesNotContain(values, t => t.Key is "url.query" or "user_agent.original" or "client.address");
        Assert.DoesNotContain(values, t => t.Value.Contains("zuzanatelemetry", StringComparison.OrdinalIgnoreCase)
            || t.Value.Contains("TelemetryProbe", StringComparison.Ordinal)
            || t.Value.Contains("needs_review", StringComparison.Ordinal)
            || t.Value.Contains(me.TenantId.ToString(), StringComparison.OrdinalIgnoreCase));
    }
}
