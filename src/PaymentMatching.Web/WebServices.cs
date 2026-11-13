using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.WebEncoders;
using OpenTelemetry.Trace;
using PaymentMatching.Data;
using PaymentMatching.Observability;

namespace PaymentMatching.Web;

/// <summary>The web app's services: data access through the tenant context, sign-in, anti-forgery, rate limits.</summary>
public static class WebServices
{
    public static IServiceCollection AddPaymentsWeb(this IServiceCollection services, IConfiguration config, string connectionString)
    {
        var operations = OperationsDb.Options(connectionString);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(PaymentsDb.Options(connectionString));
        services.AddSingleton(operations);
        services.AddDatabaseKeyRing(operations);
        services.AddSingleton<UsageLedger>();
        services.AddSingleton<TenantAdmin>();
        services.AddSingleton(AiBudgetOptions.Read(key => config[key]));
        services.AddSingleton(AdminAccounts.From(config));

        // The tenant of a request is set from the sign-in cookie (Program.cs); every query is filtered by it.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<PaymentsDbContext>();
        services.AddScoped<ReviewActions>();

        services.AddSingleton(new SandboxOptions
        {
            StarterPayments = config.GetValue("Sandboxes:StarterPayments", 40),
            MaxNewPerDay = config.GetValue("Sandboxes:MaxNewPerDay", 5),
        });

        // A new sandbox takes the AI decisions for the week's starter set from the response cache the daily job
        // filled ("live" answers in production); never a model call (ADR 0017). "off" disables it.
        var instantAiSource = config["Sandboxes:InstantAiSource"] ?? "live";
        services.AddSingleton(sp => new SandboxProvisioner(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.DbContextOptions<PaymentsDbContext>>(),
            sp.GetRequiredService<SandboxOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            instantAiSource is "off" or ""
                ? null
                : new InstantAiOptions(operations, instantAiSource, sp.GetRequiredService<AiBudgetOptions>().SmallModelOnly)));

        services.AddPaymentsTelemetry("payments-web", config, tracing => tracing.AddAspNetCoreInstrumentation(o =>
            o.Filter = http => !Path.HasExtension(http.Request.Path.Value)));
        services.AddRazorComponents();
        services.AddHttpContextAccessor();

        // Czech letters as text, not &#x...; entities (HTML encoding of markup characters stays on).
        services.Configure<WebEncoderOptions>(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Host-pm-af";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.FormFieldName = "__af";
        });
        services.AddPaymentsAuthentication(config);
        services.AddAuthorization();
        services.AddRateLimiter(AddRateLimits);
        return services;
    }

    private static void AddRateLimits(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("sign-in", http => PerMinute(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", 10));

        // The public read-only demo: anonymous, so limited per address.
        options.AddPolicy("demo", http => PerMinute(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", 60));

        // A person's decisions: generous for a human, a wall for a script.
        options.AddPolicy("actions", http => PerMinute(Authentication.TenantOf(http.User)?.ToString() ?? "anonymous", 60));
    }

    private static RateLimitPartition<string> PerMinute(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1) });
}
