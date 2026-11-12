using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Web;

// The hosted app (phase 7): sign-in with Microsoft or Google, one sandbox per person, read-only API
// over the person's own data. Visitors never trigger a model and never send text to one.
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var connectionString = config.GetConnectionString(PaymentsDb.ConnectionStringName);
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Set ConnectionStrings:Payments (user-secrets, environment ConnectionStrings__Payments or appsettings).");
}

var devLogin = Authentication.DevLoginEnabled(config, builder.Environment);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(PaymentsDb.Options(connectionString));
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<PaymentsDbContext>();
builder.Services.AddSingleton(new SandboxOptions
{
    StarterPayments = config.GetValue("Sandboxes:StarterPayments", 20),
    MaxNewPerDay = config.GetValue("Sandboxes:MaxNewPerDay", 50),
});
builder.Services.AddSingleton<SandboxProvisioner>();
builder.Services.AddPaymentsAuthentication(config);
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("sign-in", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

if (config.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var db = new PaymentsDbContext(app.Services.GetRequiredService<DbContextOptions<PaymentsDbContext>>(), new TenantContext());
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.Use(SecurityHeaders.AddAsync);
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

var providers = Authentication.ConfiguredProviders(config);

app.MapGet("/", async (HttpContext http, PaymentsDbContext db, string? full) =>
    Results.Content(
        Authentication.TenantOf(http.User) is null
            ? Pages.SignIn(providers, devLogin, full == "1")
            : Pages.Sandbox(await SandboxQueries.MeAsync(db, http.RequestAborted)),
        "text/html; charset=utf-8"));

app.MapGet("/privacy", () => Results.Content(Pages.Privacy(), "text/html; charset=utf-8"));

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

app.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/me", (PaymentsDbContext db, HttpContext http) => SandboxQueries.MeAsync(db, http.RequestAborted));
api.MapGet("/payments", (PaymentsDbContext db, HttpContext http) => SandboxQueries.PaymentsAsync(db, http.RequestAborted));
api.MapGet("/payments/{id}", async (string id, PaymentsDbContext db, HttpContext http) =>
    await SandboxQueries.PaymentAsync(db, id, http.RequestAborted) is { } payment ? Results.Ok(payment) : Results.NotFound());

await app.RunAsync();

/// <summary>Entry point, visible to the in-memory test host.</summary>
public partial class Program;
