using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PaymentMatching.Data;
using PaymentMatching.Web;

// The hosted app: sign-in with Microsoft or Google, one sandbox per person (ADR 0011), server-rendered pages
// and four actions on existing data: confirm, reject, match manually and undo an AI match (ADR 0012, 0018).
// Visitors never trigger a model and never send text to one.
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var connectionString = config.GetConnectionString(PaymentsDb.ConnectionStringName);
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Set ConnectionStrings:Payments (user-secrets, environment ConnectionStrings__Payments or appsettings).");
}

var devLogin = Authentication.DevLoginEnabled(config, builder.Environment);

builder.Services.AddPaymentsWeb(config, connectionString);

var app = builder.Build();

if (config.GetValue<bool>("Database:MigrateOnStartup"))
{
    await OperationsDb.MigrateAllAsync(connectionString);
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.Use(SecurityHeaders.AddAsync);
app.UseStaticFiles();

// English by default; Czech when the language cookie says so (set by /lang/{code}). Never from the URL of a page.
app.UseRequestLocalization(Languages.Options());
app.UseRateLimiter();
app.UseAuthentication();

// The tenant of every request comes only from the signed-in user's cookie, never from the URL.
app.Use(async (http, next) =>
{
    if (Authentication.TenantOf(http.User) is { } tenantId)
    {
        http.RequestServices.GetRequiredService<TenantContext>().Set(tenantId);
    }

    await next(http);
});
app.UseAuthorization();
app.UseAntiforgery();

var providers = Authentication.ConfiguredProviders(config);

app.MapGet("/login/{provider}", (string provider) =>
        providers.Contains(provider)
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [provider])
            : Results.NotFound())
    .RequireRateLimiting("sign-in");

if (devLogin)
{
    // Development only (checked at start-up): sign in as a local test person without an identity provider.
    app.MapGet("/dev-login", async (HttpContext http, string? name) =>
        {
            if (name is null || name.Length is 0 or > 32 || !name.All(char.IsAsciiLetterOrDigit))
            {
                return Results.BadRequest("Use /dev-login?name=alice (letters and digits, max 32).");
            }

            var principal = await Authentication.SignInPrincipalAsync(http, new SignInIdentity(Authentication.Dev, name, name, $"{name}@dev.local"));
            if (principal is null)
            {
                return Results.Redirect("/?full=1");
            }

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return Results.Redirect("/");
        })
        .RequireRateLimiting("sign-in");
}

app.MapUi(providers, devLogin);
app.MapOperations();
app.MapLanguages();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/me", (PaymentsDbContext db, HttpContext http) => SandboxQueries.MeAsync(db, http.RequestAborted));
api.MapGet("/payments", (PaymentsDbContext db, HttpContext http) => SandboxQueries.PaymentsAsync(db, http.RequestAborted));
api.MapGet("/payments/{id}", async (string id, PaymentsDbContext db, HttpContext http) =>
    await SandboxQueries.PaymentAsync(db, id, http.RequestAborted) is { } payment ? Results.Ok(payment) : Results.NotFound());

await app.RunAsync();

/// <summary>Entry point, visible to the in-memory test host.</summary>
public partial class Program;
