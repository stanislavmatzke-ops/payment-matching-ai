using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenTelemetry.Trace;
using PaymentMatching.Matching;
using PaymentMatching.Observability;

namespace PaymentMatching.Mcp.Tests;

/// <summary>
/// The MCP SDK traces every tool call, and puts a failed tool's answer (which echoes the agent's arguments)
/// into the span status. Through the hosts' pipeline (redaction) nothing an agent sent may be exported.
/// </summary>
public sealed class McpTelemetryTests
{
    private const string Marker = "Telemetricka";

    [Fact]
    public async Task Tool_calls_are_traced_by_name_without_arguments_or_error_text()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sample = SampleWorkspace.Create();
        var spans = new List<Activity>();
        var toServer = new Pipe();
        var toClient = new Pipe();

        var builder = Host.CreateApplicationBuilder();
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("Telemetry:Enabled", "true")]).Build();
        builder.Services.AddPaymentsTelemetry("payments-mcp-test", config)
            .ConfigureOpenTelemetryTracerProvider(b => b.AddInMemoryExporter(spans));
        builder.Services.AddPaymentsMcp(sample.Workspace).WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        using var server = builder.Build();
        await server.StartAsync(ct);
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: ct))
        {
            var payment = sample.PaymentsWith(PaymentStatus.HumanReview).First();
            await client.CallToolAsync(
                "propose_match",
                new Dictionary<string, object?> { ["paymentId"] = payment.Id, ["invoiceNumbers"] = new[] { "2026-9876" }, ["reason"] = $"Zuzana {Marker} said so" },
                cancellationToken: ct);
            await client.CallToolAsync("search_invoices", new Dictionary<string, object?> { ["query"] = Marker }, cancellationToken: ct);
            var failed = await client.CallToolAsync("get_payment", new Dictionary<string, object?> { ["id"] = $"PX-{Marker}" }, cancellationToken: ct);
            Assert.True(failed.IsError);
        }

        await server.StopAsync(ct);

        List<Activity> exported;
        lock (spans)
        {
            exported = [.. spans];
        }

        var tools = exported.Select(s => s.GetTagItem("gen_ai.tool.name") as string).ToList();
        Assert.Contains("propose_match", tools);
        Assert.Contains("get_payment", tools);
        Assert.Contains(exported, s => s.Status == ActivityStatusCode.Error && Equals(s.GetTagItem("gen_ai.tool.name"), "get_payment"));

        var text = exported.SelectMany(s => s.TagObjects.Select(t => Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "")
            .Append(s.DisplayName)
            .Append(s.StatusDescription ?? "")
            .Concat(s.Events.SelectMany(e => e.Tags.Select(t => Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "").Append(e.Name))))
            .ToList();
        Assert.DoesNotContain(text, t => t.Contains(Marker, StringComparison.OrdinalIgnoreCase) || t.Contains("2026-9876", StringComparison.Ordinal));
    }
}
