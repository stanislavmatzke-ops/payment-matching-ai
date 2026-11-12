using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Web.Components;

namespace PaymentMatching.Web;

/// <summary>Form of "Confirm": which proposal, and the warnings the person ticked.</summary>
public sealed class ConfirmForm
{
    public string? Proposal { get; set; }

    public string[]? Ack { get; set; }
}

/// <summary>Form of "Match manually": invoices picked from the list, and the warnings the person ticked.</summary>
public sealed class MatchForm
{
    public string[]? Invoice { get; set; }

    public string[]? Ack { get; set; }
}

/// <summary>
/// What a visitor may send. There is no free text anywhere: ids of existing rows (checked against the
/// person's own sandbox afterwards), check codes from a fixed list, and enum values.
/// </summary>
public static partial class Input
{
    public const int MaxValues = 8;

    public static bool IsId(string? value) => value is not null && IdPattern().IsMatch(value);

    public static bool AreIds(string[]? values) => values is null || (values.Length <= MaxValues && values.All(IsId));

    public static bool AreChecks(string[]? values) => values is null || (values.Length <= MaxValues && values.All(KnownChecks.Contains));

    public static ReviewOutcome? Outcome(string? value) =>
        value is not null && Enum.TryParse<ReviewOutcome>(value, out var outcome) && Enum.IsDefined(outcome) && !char.IsAsciiDigit(value[0]) ? outcome : null;

    private static readonly HashSet<string> KnownChecks = typeof(PaymentMatching.Matching.Rejections)
        .GetFields()
        .Select(f => (string)f.GetValue(null)!)
        .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9-]{1,40}$")]
    private static partial Regex IdPattern();
}

/// <summary>The pages (GET, server-rendered) and the person's three actions (POST, anti-forgery protected).</summary>
public static class UiEndpoints
{
    public static void MapUi(this WebApplication app, IReadOnlyList<string> providers, bool devLogin)
    {
        app.MapGet("/", async (HttpContext http, PaymentsDbContext db, UsageLedger ledger, AiBudgetOptions budget, TimeProvider time, string? full) =>
        {
            if (Authentication.TenantOf(http.User) is null)
            {
                return Html.Page<SignInPage>(new { Providers = providers, DevLogin = devLogin, SandboxesFull = full == "1", DemoAvailable = Operations.DemoTenantId(app.Configuration) is not null });
            }

            var me = await SandboxQueries.MeAsync(db, http.RequestAborted);
            var state = await SandboxState.LoadAsync(db, null, http.RequestAborted);
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            var ai = await Operations.BudgetAsync(state, ledger, budget, today, http.RequestAborted);
            return Html.Page<DashboardPage>(new { Page = PageContext.For(http, me.Name), Me = me, Board = SandboxQueries.Dashboard(state), Budget = ai });
        });

        app.MapGet("/privacy", () => Html.Page<PrivacyPage>(new { }));

        app.MapGet("/payments", (HttpContext http, PaymentsDbContext db, string? state) => SignedInAsync(http, db, (page, sandbox) =>
        {
            var filter = state is not null && PaymentState.All.Contains(state) ? state : null;
            var items = SandboxQueries.Items(sandbox).Where(p => filter is null || p.State == filter).ToList();
            return Html.Page<PaymentsPage>(new { Page = page, Items = items, Filter = filter });
        }));

        app.MapGet("/queue", (HttpContext http, PaymentsDbContext db) => SignedInAsync(http, db, (page, sandbox) =>
            Html.Page<PaymentsPage>(new
            {
                Page = page,
                Items = SandboxQueries.Queue(sandbox),
                Title = "Waiting for a person",
                ShowFilter = false,
                Empty = "Nothing waits for you. New payments arrive with the daily run.",
            })));

        app.MapGet("/payments/{id}", (HttpContext http, PaymentsDbContext db, string id, string? result) => SignedInAsync(http, db, (page, sandbox) =>
            Input.IsId(id) && SandboxQueries.Detail(sandbox, id, page.UserName) is { } detail
                ? Html.Page<PaymentPage>(new { Page = page, Detail = detail, Result = Input.Outcome(result) })
                : Results.NotFound()));

        app.MapGet("/payments/{id}/match", (HttpContext http, PaymentsDbContext db, ReviewActions actions, string id, string[]? invoice, string? result) =>
            SignedInAsync(http, db, async (page, sandbox) =>
            {
                if (!Input.IsId(id) || sandbox.Payment(id) is null || !Input.AreIds(invoice))
                {
                    return Results.NotFound();
                }

                if (!sandbox.IsActionable(id))
                {
                    return Results.Redirect($"/payments/{id}");
                }

                var selected = invoice?.Distinct(StringComparer.Ordinal).ToList() ?? [];
                var preview = selected.Count == 0 ? null : await actions.PreviewAsync(id, selected, http.RequestAborted);
                return Html.Page<MatchPage>(new
                {
                    Page = page,
                    Payment = SandboxQueries.Items(sandbox).Single(p => p.Id == id),
                    Invoices = SandboxQueries.OpenInvoices(sandbox),
                    Selected = selected,
                    Preview = preview,
                    Result = Input.Outcome(result),
                });
            }));

        // The person's writes. Signed in only, anti-forgery token required, rate limited per person.
        var writes = app.MapGroup("/payments/{id}")
            .RequireAuthorization()
            .RequireRateLimiting("actions")
            .WithMetadata(new RequireAntiforgeryTokenAttribute())
            .AddEndpointFilter(RequireValidAntiforgeryAsync);

        writes.MapPost("/confirm", async (string id, [FromForm] ConfirmForm form, ReviewActions actions, HttpContext http) =>
        {
            if (!Input.IsId(id) || !Input.IsId(form.Proposal) || !Input.AreChecks(form.Ack))
            {
                return Results.BadRequest();
            }

            return AfterAction(id, await actions.ConfirmAsync(id, form.Proposal!, form.Ack ?? [], http.RequestAborted));
        });

        writes.MapPost("/reject", async (string id, ReviewActions actions, HttpContext http) =>
            Input.IsId(id) ? AfterAction(id, await actions.RejectAsync(id, http.RequestAborted)) : Results.BadRequest());

        writes.MapPost("/match", async (string id, [FromForm] MatchForm form, ReviewActions actions, HttpContext http) =>
        {
            if (!Input.IsId(id) || !Input.AreIds(form.Invoice) || !Input.AreChecks(form.Ack))
            {
                return Results.BadRequest();
            }

            var invoices = form.Invoice ?? [];
            var result = await actions.MatchManuallyAsync(id, invoices, form.Ack ?? [], http.RequestAborted);
            if (result.Outcome is ReviewOutcome.Blocked or ReviewOutcome.NeedsAcknowledgement or ReviewOutcome.InvalidInvoices)
            {
                // Back to the preview, which shows today's checks again.
                var query = string.Concat(invoices.Select(n => $"invoice={Uri.EscapeDataString(n)}&"));
                return Results.Redirect($"/payments/{id}/match?{query}result={result.Outcome}");
            }

            return AfterAction(id, result);
        });

        app.MapPost("/logout", async (HttpContext http) =>
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect("/");
            })
            .WithMetadata(new RequireAntiforgeryTokenAttribute())
            .AddEndpointFilter(RequireValidAntiforgeryAsync);
    }

    private static IResult AfterAction(string id, ReviewResult result) => result.Outcome == ReviewOutcome.NotFound
        ? Results.NotFound()
        : Results.Redirect($"/payments/{id}?result={result.Outcome}");

    private static Task<IResult> SignedInAsync(HttpContext http, PaymentsDbContext db, Func<PageContext, SandboxState, IResult> render) =>
        SignedInAsync(http, db, (page, sandbox) => Task.FromResult(render(page, sandbox)));

    private static async Task<IResult> SignedInAsync(HttpContext http, PaymentsDbContext db, Func<PageContext, SandboxState, Task<IResult>> render)
    {
        if (Authentication.TenantOf(http.User) is null)
        {
            return Results.Redirect("/");
        }

        var name = await db.Users.AsNoTracking().Select(u => u.Name).SingleAsync(http.RequestAborted);
        var sandbox = await SandboxState.LoadAsync(db, null, http.RequestAborted);
        return await render(PageContext.For(http, name), sandbox);
    }

    /// <summary>
    /// The anti-forgery middleware only records whether the token was valid; endpoints that read a form
    /// fail on their own, the others (reject, sign out) are stopped here.
    /// </summary>
    private static async ValueTask<object?> RequireValidAntiforgeryAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var feature = context.HttpContext.Features.Get<IAntiforgeryValidationFeature>();
        return feature is { IsValid: true } ? await next(context) : Results.BadRequest();
    }
}
