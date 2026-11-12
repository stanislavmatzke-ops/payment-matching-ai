using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;

namespace PaymentMatching.Web;

/// <summary>The AI budget as one sandbox sees it: its own calls today and whether the app's budget is available.</summary>
public sealed record AiBudgetView(int SandboxCallsToday, int SandboxCallsPerDay, BudgetLimit App, bool SmallModelOnly);

/// <summary>One day of the app-wide ledger.</summary>
public sealed record UsageDay(DateOnly Day, UsageTotals Totals);

/// <summary>
/// The admin overview (ADR 0013): aggregates across sandboxes only – counts and sums, never an id,
/// a name, an e-mail or a payment.
/// </summary>
public sealed record AdminView(SandboxSummary Sandboxes, UsageTotals Today, UsageTotals Month, AiBudgetOptions Budget, BudgetLimit App, IReadOnlyList<UsageDay> LastDays)
{
    public decimal MonthLeftUsd => Math.Max(0m, Budget.MonthlyUsd - Month.AiCostUsd);

    public int CallsLeftToday => Math.Max(0, Budget.CallsPerDay - Today.AiCalls);
}

/// <summary>Accounts allowed to see <c>/admin</c>: <c>Admin:Accounts</c> in configuration, as "provider:subject".</summary>
public sealed class AdminAccounts(IEnumerable<string> accounts)
{
    private readonly HashSet<string> _accounts = accounts.Where(a => a.Contains(':', StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);

    public static AdminAccounts From(IConfiguration config) =>
        new(config.GetSection("Admin:Accounts").GetChildren().Select(c => c.Value ?? ""));

    public bool Contains(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.FindFirstValue(ClaimTypes.NameIdentifier) is { } id
        && _accounts.Contains(id);
}

/// <summary>Reads for the budget state, the admin overview and the read-only demo.</summary>
public static class Operations
{
    public static async Task<AiBudgetView> BudgetAsync(SandboxState state, UsageLedger ledger, AiBudgetOptions budget, DateOnly today, CancellationToken cancellationToken)
    {
        var callsToday = state.Decisions
            .Where(d => DateOnly.FromDateTime(d.At.UtcDateTime) == today)
            .Sum(d => d.Calls.Count(c => !c.Cached));
        var app = AiAllowance.AppState(budget, await ledger.DayAsync(today, cancellationToken), await ledger.MonthAsync(today, cancellationToken));
        return new AiBudgetView(callsToday, budget.CallsPerSandboxPerDay, app, budget.SmallModelOnly);
    }

    public static async Task<AdminView> AdminAsync(TenantAdmin admin, UsageLedger ledger, AiBudgetOptions budget, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var todayTotals = await ledger.DayAsync(today, cancellationToken);
        var month = await ledger.MonthAsync(today, cancellationToken);
        var days = new List<UsageDay>();
        for (var i = 6; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            days.Add(new UsageDay(day, await ledger.DayAsync(day, cancellationToken)));
        }

        return new AdminView(await admin.SummaryAsync(now, cancellationToken), todayTotals, month, budget, AiAllowance.AppState(budget, todayTotals, month), days);
    }

    /// <summary>
    /// The demo sandbox, read-only, or null. The id comes from server configuration, never from the request,
    /// and the sandbox must be marked as the demo: a configuration pointing at a person's sandbox shows nothing.
    /// </summary>
    public static async Task<SandboxState?> DemoAsync(DbContextOptions<PaymentsDbContext> options, Guid? demoId, CancellationToken cancellationToken)
    {
        if (demoId is not { } id)
        {
            return null;
        }

        await using var db = new PaymentsDbContext(options, TenantContext.ReadOnlyFor(id));
        if (!await db.Tenants.AnyAsync(t => t.IsDemo, cancellationToken))
        {
            return null;
        }

        return await SandboxState.LoadAsync(db, null, cancellationToken);
    }

    public static Guid? DemoTenantId(IConfiguration config) =>
        Guid.TryParse(config["Demo:TenantId"], out var id) && id != Guid.Empty ? id : null;
}
