using System.Globalization;
using PaymentMatching.Data;
using PaymentMatching.Mcp;

// MCP server "payments-mcp" over one workspace: either a dataset directory with the pipeline's state
// directory, or one tenant (sandbox) of the app database.
//
//   payments-mcp [--data <dir>] [--state <dir>] [--tenant <id>] [--transport stdio|http] [--port <n>]
//
//   --data       customers.csv, invoices.csv, bank_statement.csv (default data/sample)
//   --state      audit.jsonl from Match.Cli (read only) and proposals.jsonl (written) (default out/match)
//   --tenant     instead of --data/--state: the sandbox id (shown at /api/me) in the app database;
//                connection string ConnectionStrings:Payments from user-secrets or ConnectionStrings__Payments.
//                Who starts the process chooses the tenant; no tool can (ADR 0010, 0011).
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
var tenantArg = named.GetValueOrDefault("--tenant");
var transport = named.GetValueOrDefault("--transport", "stdio");
var port = int.Parse(named.GetValueOrDefault("--port", "5100"), CultureInfo.InvariantCulture);
Guid tenantId = default;

if (args.Length % 2 != 0
    || transport is not ("stdio" or "http")
    || (tenantArg is null && !Directory.Exists(dataDir))
    || (tenantArg is not null && !Guid.TryParse(tenantArg, out tenantId)))
{
    Console.Error.WriteLine("Usage: payments-mcp [--data <dir>] [--state <dir>] [--tenant <id>] [--transport stdio|http] [--port <n>]");
    Console.Error.WriteLine($"Data directory: {Path.GetFullPath(dataDir)}");
    return 2;
}

Workspace workspace;
if (tenantArg is null)
{
    workspace = Workspace.Load(dataDir, stateDir);
}
else
{
    var config = new ConfigurationBuilder().AddUserSecrets("payment-matching-ai").AddEnvironmentVariables().Build();
    var connectionString = config.GetConnectionString(PaymentsDb.ConnectionStringName);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.Error.WriteLine("Set ConnectionStrings:Payments: dotnet user-secrets set \"ConnectionStrings:Payments\" \"<connection string>\" --id payment-matching-ai");
        return 2;
    }

    try
    {
        workspace = await Workspace.LoadAsync(PaymentsDb.ForTenant(connectionString, tenantId));
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

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
