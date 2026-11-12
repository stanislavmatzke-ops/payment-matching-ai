using System.Globalization;
using System.Text.Encodings.Web;

namespace PaymentMatching.Web;

/// <summary>
/// The few server-rendered pages of phase 7 (the real UI comes in phase 8). Everything dynamic is
/// HTML-encoded; there are no scripts and no inline styles, so the CSP can forbid both.
/// </summary>
public static class Pages
{
    private static readonly HtmlEncoder Html = HtmlEncoder.Default;

    public static string SignIn(IReadOnlyList<string> providers, bool devLogin, bool sandboxesFull)
    {
        var buttons = string.Concat(providers.Select(p =>
            $"<li><a href=\"/login/{Html.Encode(p)}\">Sign in with {(p == Authentication.Microsoft ? "Microsoft" : "Google")}</a></li>"));
        var dev = devLogin
            ? "<li><form method=\"get\" action=\"/dev-login\"><label>Development sign-in as <input name=\"name\" value=\"alice\" pattern=\"[A-Za-z0-9]{1,32}\" required></label> <button>Sign in</button></form></li>"
            : "";
        var none = providers.Count == 0 && !devLogin
            ? "<p>No sign-in method is configured. See README: \"Sign-in (local)\".</p>"
            : "";
        var full = sandboxesFull
            ? "<p><strong>Today's new sandboxes are taken.</strong> Please try again tomorrow.</p>"
            : "";
        return Layout("Sign in", $"""
            <h1>Payment matching with AI</h1>
            <p>Sign in to get your own sandbox: a fictional company with invoices and bank payments
            (synthetic data, Demo Bank). Nobody else sees your sandbox.</p>
            {full}{none}
            <ul>{buttons}{dev}</ul>
            """);
    }

    public static string Sandbox(MeView me)
    {
        var c = me.Counts;
        static string Row(string label, int value) => $"<tr><th scope=\"row\">{label}</th><td>{value.ToString(CultureInfo.InvariantCulture)}</td></tr>";
        return Layout("Your sandbox", $"""
            <h1>Your sandbox</h1>
            <p>Signed in as {Html.Encode(me.Name)} ({Html.Encode(me.Email)}) via {Html.Encode(me.Provider)}.</p>
            <table>
            {Row("Customers", c.Customers)}
            {Row("Invoices", c.Invoices)}
            {Row("Payments", c.Payments)}
            {Row("Matched automatically", c.AutoMatched)}
            {Row("Waiting for a person", c.HumanReview)}
            {Row("Not matched on purpose", c.NoMatch)}
            {Row("Waiting for the daily AI run", c.WaitingForAi)}
            </table>
            <p>Data: <a href="/api/payments">/api/payments</a>, <a href="/api/me">/api/me</a>. The full UI comes later.</p>
            <form method="post" action="/logout"><button>Sign out</button></form>
            """);
    }

    public static string Privacy() => Layout("Privacy", """
        <h1>Privacy</h1>
        <p>This is a demo application. All invoices and payments in it are synthetic.</p>
        <h2>What we keep</h2>
        <ul>
        <li>From your sign-in with Microsoft or Google: the provider, your account id at that provider (subject),
        your name and e-mail address. Nothing else; no access tokens are stored.</li>
        <li>Your sandbox: generated data and the decisions you or the application make in it.</li>
        </ul>
        <h2>Cookies</h2>
        <p>Only cookies needed to sign you in: the sign-in session cookie and short-lived cookies used during the
        sign-in redirect. No analytics, no advertising.</p>
        <h2>Deletion</h2>
        <p>A sandbox that nobody used for 30 days is deleted with everything in it, including your name and e-mail.</p>
        <h2>AI</h2>
        <p>You never send text to an AI model. A scheduled job processes only generated data.</p>
        """);

    private static string Layout(string title, string body) => $"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{Html.Encode(title)} – Payment matching</title></head>
        <body>
        <main>
        {body}
        </main>
        <footer><a href="/">Home</a> · <a href="/privacy">Privacy</a></footer>
        </body>
        </html>
        """;
}

/// <summary>Browser hardening for every response.</summary>
public static class SecurityHeaders
{
    public static Task AddAsync(HttpContext http, Func<Task> next)
    {
        var headers = http.Response.Headers;
        headers.ContentSecurityPolicy = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        return next();
    }
}
