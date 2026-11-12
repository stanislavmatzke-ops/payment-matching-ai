using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Web;

// The hosted app: sign-in with Microsoft or Google, one sandbox per person (phase 7), server-rendered
// pages and three actions on existing data: confirm, reject, match manually (phase 8). Visitors never
// trigger a model and never send text to one.
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
builder.Services.AddSingleton(OperationsDb.Options(connectionString));
builder.Services.AddSingleton<UsageLedger>();
builder.Services.AddSingleton<TenantAdmin>();
builder.Services.AddSingleton(AiBudgetOptions.Read(key => config[key]));
builder.Services.AddSingleton(AdminAccounts.From(config));
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<PaymentsDbContext>();
builder.Services.AddSingleton(new SandboxOptions
{
    StarterPayments = config.GetValue("Sandboxes:StarterPayments", 20),
    MaxNewPerDay = config.GetValue("Sandboxes:MaxNewPerDay", 5),
});
builder.Services.AddSingleton<SandboxProvisioner>();
builder.Services.AddScoped(sp => new ReviewActions(sp.GetRequiredService<PaymentsDbContext>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddRazorComponents();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-pm-af";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.FormFieldName = "__af";
});
builder.Services.AddPaymentsAuthentication(config);
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("sign-in", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));

    // The public read-only demo: anonymous, so limited per address.
    options.AddPolicy("demo", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));

    // A person's decisions: generous for a human, a wall for a script.
    options.AddPolicy("actions", http => RateLimitPartition.GetFixedWindowLimiter(
        Authentication.TenantOf(http.User)?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

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

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/me", (PaymentsDbContext db, HttpContext http) => SandboxQueries.MeAsync(db, http.RequestAborted));
api.MapGet("/payments", (PaymentsDbContext db, HttpContext http) => SandboxQueries.PaymentsAsync(db, http.RequestAborted));
api.MapGet("/payments/{id}", async (string id, PaymentsDbContext db, HttpContext http) =>
    await SandboxQueries.PaymentAsync(db, id, http.RequestAborted) is { } payment ? Results.Ok(payment) : Results.NotFound());

await app.RunAsync();

/// <summary>Entry point, visible to the in-memory test host.</summary>
public partial class Program;
