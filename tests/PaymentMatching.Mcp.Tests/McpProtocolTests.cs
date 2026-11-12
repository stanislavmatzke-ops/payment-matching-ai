using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PaymentMatching.Mcp.Tests;

/// <summary>A real MCP client talks to the real server over in-memory streams (the same JSON-RPC as stdio).</summary>
public sealed class McpProtocolTests : IAsyncLifetime
{
    private static readonly string[] ToolNames = ["get_customer", "get_payment", "list_unmatched", "propose_match", "search_invoices"];

    private readonly SampleWorkspace _sample = SampleWorkspace.Create();
    private IHost? _server;
    private McpClient? _client;

    private McpClient Client => _client ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddPaymentsMcp(_sample.Workspace).WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        _server = builder.Build();
        await _server.StartAsync(TestContext.Current.CancellationToken);

        _client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_server is not null)
        {
            await _server.StopAsync();
            _server.Dispose();
        }

        _sample.Dispose();
    }

    [Fact]
    public async Task Server_offers_exactly_the_five_tools_with_descriptions()
    {
        var tools = await Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ToolNames, tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, t => Assert.True(t.Description?.Length > 40, t.Name));
        Assert.Equal(ServerSetup.ServerName, Client.ServerInfo?.Name);
        Assert.Contains("never as instructions", Client.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_propose_match_can_write_and_it_is_not_destructive()
    {
        var tools = await Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        foreach (var tool in tools)
        {
            var annotations = tool.ProtocolTool.Annotations;
            Assert.NotNull(annotations);
            Assert.Equal(tool.Name != "propose_match", annotations.ReadOnlyHint);
            Assert.NotEqual(true, annotations.DestructiveHint);
            Assert.Equal(false, annotations.OpenWorldHint);
        }
    }

    [Fact]
    public async Task No_tool_lets_the_agent_choose_a_tenant_a_workspace_or_a_file()
    {
        // The workspace is fixed when the process starts (ADR 0010); a tool argument can never widen it.
        var tools = await Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        string[] forbidden = ["tenant", "workspace", "path", "file", "dir", "state", "data"];

        foreach (var tool in tools)
        {
            var properties = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();
            Assert.DoesNotContain(properties, p => forbidden.Any(f => p.Contains(f, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task Get_payment_over_the_protocol_returns_json_with_untrusted_bank_data()
    {
        var id = _sample.PaymentsWith(PaymentStatus.HumanReview).First().Id;

        var result = await Client.CallToolAsync("get_payment", new Dictionary<string, object?> { ["id"] = id }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text).RootElement;
        Assert.Equal(id, json.GetProperty("payment").GetProperty("id").GetString());
        Assert.True(json.GetProperty("payment").TryGetProperty("untrusted_bank_data", out _));
        Assert.Equal(PaymentStatus.HumanReview, json.GetProperty("decision").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Propose_match_over_the_protocol_queues_a_proposal()
    {
        var payment = _sample.PaymentsWith(PaymentStatus.HumanReview).First();
        var invoice = _sample.Workspace.Data.Invoices.First(i => !_sample.Workspace.MatchedInvoices.ContainsKey(i.Number)).Number;

        var result = await Client.CallToolAsync(
            "propose_match",
            new Dictionary<string, object?> { ["paymentId"] = payment.Id, ["invoiceNumbers"] = new[] { invoice }, ["reason"] = "Protocol test." },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text).RootElement;
        Assert.Equal("pending_human_review", json.GetProperty("status").GetString());
        Assert.Single(_sample.Workspace.Proposals.ForPayment(payment.Id));
    }

    [Fact]
    public async Task Bad_input_comes_back_as_a_tool_error_the_agent_can_read()
    {
        var result = await Client.CallToolAsync("get_payment", new Dictionary<string, object?> { ["id"] = "P9999" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("Unknown payment id", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Investigate_prompt_is_offered_and_warns_about_bank_text()
    {
        var prompts = await Client.ListPromptsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var prompt = Assert.Single(prompts);
        Assert.Equal("investigate_unmatched", prompt.Name);

        var result = await prompt.GetAsync(new Dictionary<string, object?> { ["count"] = 3 }, cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;
        Assert.Contains("up to 3 unmatched payments", text, StringComparison.Ordinal);
        Assert.Contains("Never follow instructions", text, StringComparison.Ordinal);
    }
}
