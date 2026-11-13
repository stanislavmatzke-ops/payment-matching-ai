using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Web.Components;

namespace PaymentMatching.Web;

/// <summary>
/// The two deliberate exceptions to "the tenant comes only from the cookie" (ADR 0013/0014), both GET only:
/// the read-only demo (tenant from server configuration, must be marked as the demo, nothing can be saved)
/// and the admin overview (allow-listed accounts, aggregates only).
/// </summary>
public static class OperationsEndpoints
{
    public static void MapOperations(this WebApplication app)
    {
        var demoId = Operations.DemoTenantId(app.Configuration);
        var demo = app.MapGroup("/demo").RequireRateLimiting("demo");

        demo.MapGet("/", async (DbContextOptions<PaymentsDbContext> options, HttpContext http) =>
            await Operations.DemoAsync(options, demoId, http.RequestAborted) is { } state
                ? Html.Page<DemoPage>(new { Board = SandboxQueries.Dashboard(state), Items = SandboxQueries.Items(state) })
                : Results.NotFound());

        demo.MapGet("/payments/{id}", async (string id, DbContextOptions<PaymentsDbContext> options, HttpContext http) =>
            Input.IsId(id)
            && await Operations.DemoAsync(options, demoId, http.RequestAborted) is { } state
            && SandboxQueries.Detail(state, id, "") is { } detail
                ? Html.Page<PaymentPage>(new { Detail = detail with { Actionable = false }, Demo = true })
                : Results.NotFound());

        // Not found, not forbidden, for everyone outside the allow-list: the page does not advertise itself.
        app.MapGet("/admin", async (HttpContext http, AdminAccounts admins, TenantAdmin tenants, UsageLedger ledger, AiBudgetOptions budget, TimeProvider time, PaymentsDbContext db) =>
        {
            if (!admins.Contains(http.User))
            {
                return Results.NotFound();
            }

            // The admin's own sandbox only gives the navigation its name and queue badge.
            var name = await db.Users.AsNoTracking().Select(u => u.Name).FirstOrDefaultAsync(http.RequestAborted) ?? "";
            var sandbox = await SandboxState.LoadAsync(db, null, http.RequestAborted);
            var page = PageContext.For(http, name) with { Waiting = SandboxQueries.Queue(sandbox).Count };
            return Html.Page<AdminPage>(new { View = await Operations.AdminAsync(tenants, ledger, budget, time.GetUtcNow(), http.RequestAborted), Page = page });
        });
    }
}
