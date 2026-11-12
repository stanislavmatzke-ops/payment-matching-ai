using System.Globalization;
using PaymentMatching.Mcp;

// MCP server "payments-mcp" over one workspace: a dataset directory and the pipeline's state directory.
//
//   payments-mcp [--data <dir>] [--state <dir>] [--transport stdio|http] [--port <n>]
//
//   --data       customers.csv, invoices.csv, bank_statement.csv (default data/sample)
//   --state      audit.jsonl from Match.Cli (read only) and proposals.jsonl (written) (default out/match)
//   --transport  stdio (default; Claude Code / Claude Desktop start the process) or http (Streamable HTTP
//                on http://localhost:<port>/mcp, loopback only, no authentication – local use only)
//
// The server never calls a model and never writes a match (ADR 0010).
var named = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length - 1; i += 2)
{
    named[args[i]] = args[i + 1];
}

var dataDir = named.GetValueOrDefault("--data", Path.Combine("data", "sample"));
var stateDir = named.GetValueOrDefault("--state", Path.Combine("out", "match"));
var transport = named.GetValueOrDefault("--transport", "stdio");
var port = int.Parse(named.GetValueOrDefault("--port", "5100"), CultureInfo.InvariantCulture);

if (args.Length % 2 != 0 || transport is not ("stdio" or "http") || !Directory.Exists(dataDir))
{
    Console.Error.WriteLine("Usage: payments-mcp [--data <dir>] [--state <dir>] [--transport stdio|http] [--port <n>]");
    Console.Error.WriteLine($"Data directory: {Path.GetFullPath(dataDir)}");
    return 2;
}

var workspace = Workspace.Load(dataDir, stateDir);

if (transport == "stdio")
{
    var builder = Host.CreateApplicationBuilder();

    // stdout carries the protocol; every log line must go to stderr.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddPaymentsMcp(workspace).WithStdioServerTransport();
    await builder.Build().RunAsync();
}
else
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls($"http://localhost:{port}");

    // Loopback only, and only loopback Host headers (a web page cannot reach it via DNS rebinding).
    builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
    builder.Services.AddPaymentsMcp(workspace).WithHttpTransport();
    var app = builder.Build();
    app.MapMcp("/mcp");
    await app.RunAsync();
}

return 0;
