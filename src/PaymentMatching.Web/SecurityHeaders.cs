namespace PaymentMatching.Web;

/// <summary>
/// Browser hardening for every response. The pages have no scripts at all and one same-origin
/// stylesheet and icon, so the CSP forbids scripts, inline styles, frames and forms to other origins.
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    public static Task AddAsync(HttpContext http, Func<Task> next)
    {
        var headers = http.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        return next();
    }
}
