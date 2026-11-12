using PaymentMatching.AiGateway;
using PaymentMatching.Matching;

namespace PaymentMatching.Mcp.Tests;

/// <summary>
/// data/sample after one pipeline run with the deterministic stub (no model, no key), in a fresh
/// temporary state directory: an audit log the server reads and an empty proposal queue.
/// </summary>
internal sealed class SampleWorkspace : IDisposable
{
    private SampleWorkspace(string stateDir)
    {
        StateDir = stateDir;
        Workspace = Workspace.Load(DataDir, stateDir);
        Queries = new PaymentQueries(Workspace);
        Expected = File.ReadAllLines(Path.Combine(DataDir, "expected.csv"))
            .Skip(1)
            .Where(l => l.Length > 0)
            .Select(MatchingDataset.SplitCsv)
            .ToDictionary(f => f[0], f => (CaseType: f[1], Invoices: f[3].Split('|', StringSplitOptions.RemoveEmptyEntries), InjectionTarget: f[5]));
    }

    public static string DataDir { get; } = RepoPaths.Combine("data", "sample");

    public string StateDir { get; }

    public string AuditPath => Path.Combine(StateDir, Workspace.AuditFile);

    public string ProposalsPath => Path.Combine(StateDir, Workspace.ProposalsFile);

    public Workspace Workspace { get; }

    public PaymentQueries Queries { get; }

    public Dictionary<string, (string CaseType, string[] Invoices, string InjectionTarget)> Expected { get; }

    public static SampleWorkspace Create()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "payments-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);
        var data = MatchingDataset.Load(DataDir);
        var gateway = new AiGateway.AiGateway(new NaiveModelStub(), ModelSettings.Default, PriceTable.Claude);
        var log = new DecisionLog(Path.Combine(stateDir, Workspace.AuditFile));
        new MatchingPipeline(data, gateway, log, MatchingOptions.Default).RunAsync().GetAwaiter().GetResult();
        return new SampleWorkspace(stateDir);
    }

    public IEnumerable<BankPayment> PaymentsWith(string status) =>
        Workspace.Data.Payments.Where(p => Workspace.Status(p.Id) == status);

    public void Dispose()
    {
        try
        {
            Directory.Delete(StateDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS cleans the temp directory.
        }
    }
}
