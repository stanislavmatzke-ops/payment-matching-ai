using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PaymentMatching.Data;

namespace PaymentMatching.Web;

/// <summary>
/// Sign-in with Microsoft or Google (OpenID Connect, authorization code + PKCE) into a cookie that
/// carries only the user's tenant. Nothing from the identity provider is kept beyond provider,
/// subject, name and e-mail (SPEC 9b); tokens are not saved.
/// </summary>
public static class Authentication
{
    public const string TenantClaim = "tenant_id";
    public const string Microsoft = "microsoft";
    public const string Google = "google";
    public const string Dev = "dev";

    /// <summary>Providers with a client id and secret in configuration (user-secrets or environment).</summary>
    public static IReadOnlyList<string> ConfiguredProviders(IConfiguration config) =>
        new[] { Microsoft, Google }.Where(p => IsConfigured(config, p)).ToList();

    public static bool DevLoginEnabled(IConfiguration config, IHostEnvironment env)
    {
        var enabled = config.GetValue<bool>("Auth:DevLogin");
        if (enabled && !env.IsDevelopment())
        {
            // A login without an identity provider must never reach a public deployment.
            throw new InvalidOperationException("Auth:DevLogin is allowed only in the Development environment.");
        }

        return enabled;
    }

    public static void AddPaymentsAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var auth = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "__Host-pm-auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.LoginPath = "/";
                options.Events.OnRedirectToLogin = context => Deny(context, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => Deny(context, StatusCodes.Status403Forbidden);
                options.Events.OnValidatePrincipal = ValidateTenantAsync;
            });

        if (IsConfigured(config, Microsoft))
        {
            auth.AddOpenIdConnect(Microsoft, "Microsoft", options =>
            {
                Configure(options, config, Microsoft, "/signin-microsoft");

                // Work, school and personal accounts. The issuer then depends on the user's
                // directory, so it is checked against the token's own tenant id (tid).
                options.Authority = "https://login.microsoftonline.com/common/v2.0";
                options.TokenValidationParameters.IssuerValidator = MicrosoftIssuer.Validate;
            });
        }

        if (IsConfigured(config, Google))
        {
            auth.AddOpenIdConnect(Google, "Google", options =>
            {
                Configure(options, config, Google, "/signin-google");
                options.Authority = "https://accounts.google.com";
            });
        }
    }

    /// <summary>
    /// Finds or creates the person's sandbox and builds the cookie principal: subject, name and tenant.
    /// Null when the daily limit of new sandboxes is reached.
    /// </summary>
    public static async Task<ClaimsPrincipal?> SignInPrincipalAsync(HttpContext http, SignInIdentity identity)
    {
        var provisioner = http.RequestServices.GetRequiredService<SandboxProvisioner>();
        var result = await provisioner.EnsureAsync(identity, http.RequestAborted);
        if (result.TenantId is not { } tenantId)
        {
            // Counted for the admin overview (the waiting list); nothing about the person is kept.
            var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            await http.RequestServices.GetRequiredService<UsageLedger>()
                .AddAsync(DateOnly.FromDateTime(now.UtcDateTime), new UsageDelta(SignInsTurnedAway: 1), http.RequestAborted);
            return null;
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, $"{identity.Provider}:{identity.Subject}"),
            new Claim(ClaimTypes.Name, identity.Name),
            new Claim(TenantClaim, tenantId.ToString()),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static Guid? TenantOf(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && Guid.TryParse(user.FindFirstValue(TenantClaim), out var id) ? id : null;

    private static bool IsConfigured(IConfiguration config, string provider) =>
        !string.IsNullOrWhiteSpace(config[$"Auth:{Capitalized(provider)}:ClientId"])
        && !string.IsNullOrWhiteSpace(config[$"Auth:{Capitalized(provider)}:ClientSecret"]);

    private static string Capitalized(string provider) => char.ToUpperInvariant(provider[0]) + provider[1..];

    private static void Configure(OpenIdConnectOptions options, IConfiguration config, string provider, string callbackPath)
    {
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.ClientId = config[$"Auth:{Capitalized(provider)}:ClientId"];
        options.ClientSecret = config[$"Auth:{Capitalized(provider)}:ClientSecret"];
        options.CallbackPath = callbackPath;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.SaveTokens = false;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Events.OnTicketReceived = async context =>
        {
            var principal = context.Principal!;
            var subject = principal.FindFirstValue("sub");
            if (string.IsNullOrEmpty(subject))
            {
                context.Fail("The identity provider returned no subject.");
                return;
            }

            var identity = new SignInIdentity(
                provider,
                subject,
                principal.FindFirstValue("name") ?? "",
                principal.FindFirstValue("email") ?? principal.FindFirstValue("preferred_username") ?? "");
            var signedIn = await SignInPrincipalAsync(context.HttpContext, identity);
            if (signedIn is null)
            {
                context.HandleResponse();
                context.Response.Redirect("/?full=1");
                return;
            }

            context.Principal = signedIn;
        };
    }

    /// <summary>A cookie whose sandbox was deleted (30 days inactive) no longer signs anyone in.</summary>
    private static async Task ValidateTenantAsync(CookieValidatePrincipalContext context)
    {
        if (TenantOf(context.Principal!) is not { } tenantId)
        {
            context.RejectPrincipal();
            return;
        }

        var options = context.HttpContext.RequestServices.GetRequiredService<DbContextOptions<PaymentsDbContext>>();
        var time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        await using var db = new PaymentsDbContext(options, new TenantContext(tenantId));
        if (!await db.Tenants.AnyAsync(context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        await SandboxActivity.TouchAsync(db, time.GetUtcNow(), context.HttpContext.RequestAborted);
    }

    private static Task Deny(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Issuer check for the Microsoft "common" endpoint: the issuer must be
/// https://login.microsoftonline.com/{tid}/v2.0 for the tenant id inside the same token.
/// </summary>
public static class MicrosoftIssuer
{
    public static string Validate(string issuer, SecurityToken token, TokenValidationParameters parameters)
    {
        var tid = token is JsonWebToken jwt && jwt.TryGetPayloadValue<string>("tid", out var value) ? value : null;
        if (Guid.TryParse(tid, out var tenant) && issuer == $"https://login.microsoftonline.com/{tenant:D}/v2.0")
        {
            return issuer;
        }

        throw new SecurityTokenInvalidIssuerException($"Issuer '{issuer}' does not match the token's tenant.");
    }
}
