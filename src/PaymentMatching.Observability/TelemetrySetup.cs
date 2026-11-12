using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PaymentMatching.Observability;

/// <summary>
/// Where telemetry goes, from configuration only. Nothing configured = the SDK is not even started, so tests
/// and CI need no backend (ADR 0015).
/// </summary>
/// <param name="OtlpEndpoint"><c>OTEL_EXPORTER_OTLP_ENDPOINT</c>: the local Aspire dashboard or any collector.</param>
/// <param name="AzureMonitor"><c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>: Application Insights (phase 11).</param>
/// <param name="Forced"><c>Telemetry:Enabled</c>: start the SDK without an exporter (tests add an in-memory one).</param>
public sealed record TelemetrySettings(Uri? OtlpEndpoint, string? AzureMonitor, bool Forced)
{
    public bool Enabled => OtlpEndpoint is not null || AzureMonitor is not null || Forced;

    public static TelemetrySettings From(IConfiguration config)
    {
        var otlp = config["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var azure = config["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        return new(
            string.IsNullOrWhiteSpace(otlp) ? null : new Uri(otlp),
            string.IsNullOrWhiteSpace(azure) ? null : azure,
            bool.TryParse(config["Telemetry:Enabled"], out var forced) && forced);
    }
}

public static class TelemetrySetup
{
    /// <summary>
    /// Trace sources: ours, outgoing HTTP (the model API) and the MCP SDK. Npgsql spans are left out on
    /// purpose: a database error carries the offending values in its message (ADR 0015).
    /// </summary>
    public static readonly string[] Sources = [AiGateway.Telemetry.Name, "System.Net.Http", "Experimental.ModelContextProtocol"];

    /// <summary>Metric meters: numbers only, so the database and the web server are in.</summary>
    public static readonly string[] Meters =
    [
        AiGateway.Telemetry.Name, "Npgsql", "System.Net.Http", "Experimental.ModelContextProtocol",
        "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.RateLimiting",
    ];

    /// <summary>For hosts with dependency injection (web app, MCP server).</summary>
    /// <param name="tracing">Extra instrumentation of the host (ASP.NET Core); added before redaction and export.</param>
    public static IServiceCollection AddPaymentsTelemetry(
        this IServiceCollection services, string serviceName, IConfiguration config, Action<TracerProviderBuilder>? tracing = null)
    {
        var settings = TelemetrySettings.From(config);
        if (!settings.Enabled)
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(b =>
            {
                tracing?.Invoke(b);
                Tracing(b, settings);
            })
            .WithMetrics(b => Metrics(b, settings));
        return services;
    }

    /// <summary>
    /// For one-shot console hosts (daily job, Match.Cli). Dispose at the end: that flushes the last spans and
    /// metrics. Returns an empty session when nothing is configured.
    /// </summary>
    public static IDisposable StartConsole(string serviceName, IConfiguration config)
    {
        var settings = TelemetrySettings.From(config);
        if (!settings.Enabled)
        {
            return new Session([]);
        }

        var resource = ResourceBuilder.CreateDefault().AddService(serviceName);
        var tracer = Tracing(Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource), settings).Build();
        var meter = Metrics(Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource), settings).Build();
        return new Session([tracer, meter]);
    }

    private static TracerProviderBuilder Tracing(TracerProviderBuilder builder, TelemetrySettings settings)
    {
        builder.AddSource(Sources).AddProcessor(new RedactionProcessor());
        if (settings.OtlpEndpoint is { } endpoint)
        {
            builder.AddOtlpExporter(o => o.Endpoint = endpoint);
        }

        if (settings.AzureMonitor is { } connectionString)
        {
            builder.AddAzureMonitorTraceExporter(o => o.ConnectionString = connectionString);
        }

        return builder;
    }

    private static MeterProviderBuilder Metrics(MeterProviderBuilder builder, TelemetrySettings settings)
    {
        builder.AddMeter(Meters);
        if (settings.OtlpEndpoint is { } endpoint)
        {
            builder.AddOtlpExporter(o => o.Endpoint = endpoint);
        }

        if (settings.AzureMonitor is { } connectionString)
        {
            builder.AddAzureMonitorMetricExporter(o => o.ConnectionString = connectionString);
        }

        return builder;
    }

    private sealed class Session(IReadOnlyList<IDisposable?> providers) : IDisposable
    {
        public void Dispose()
        {
            foreach (var provider in providers)
            {
                provider?.Dispose();
            }
        }
    }
}
