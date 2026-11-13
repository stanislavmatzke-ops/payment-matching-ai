using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace PaymentMatching.Web;

/// <summary>
/// English (default) and Czech. The choice is a functional cookie set by <c>GET /lang/{code}</c>, which then
/// returns to the page the person came from – a local path only, so the link cannot send anyone elsewhere.
/// </summary>
public static class Languages
{
    public const string CookieName = "__Host-pm-lang";

    public static RequestLocalizationOptions Options()
    {
        var cultures = new[] { new CultureInfo("en"), new CultureInfo("cs") };
        var options = new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture("en"),
            SupportedCultures = cultures,
            SupportedUICultures = cultures,
        };
        options.RequestCultureProviders = [new CookieRequestCultureProvider { CookieName = CookieName }];
        return options;
    }

    public static void MapLanguages(this WebApplication app) =>
        app.MapGet("/lang/{code}", (HttpContext http, string code, string? @return) =>
        {
            if (!Text.Languages.Contains(code))
            {
                return Results.NotFound();
            }

            http.Response.Cookies.Append(
                CookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(code)),
                new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromDays(365), IsEssential = true });
            return Results.Redirect(IsLocalPath(@return) ? @return! : "/");
        });

    /// <summary>"/payments?state=x" yes; "//evil.example", "/\evil", "https://…" no.</summary>
    public static bool IsLocalPath(string? path) =>
        path is { Length: > 0 and <= 512 }
        && path[0] == '/'
        && (path.Length == 1 || (path[1] != '/' && path[1] != '\\'))
        && !path.Contains('\r', StringComparison.Ordinal) && !path.Contains('\n', StringComparison.Ordinal);
}
