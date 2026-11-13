using System.Net;
using System.Net.Http.Json;
using PaymentMatching.Data;
using PaymentMatching.Testing;

namespace PaymentMatching.Web.Tests;

/// <summary>Czech next to English, and the payments list's search, filters, sort and pages.</summary>
public class LanguageAndListTests(PostgresFixture postgres)
{
    private static readonly Guid DemoId = Guid.Parse("4f1e2d3c-0000-4000-8000-00000000c5c5");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_page_renders_in_czech_without_a_missing_translation()
    {
        var connectionString = await postgres.NewDatabaseAsync(Ct);
        await new SandboxProvisioner(PaymentsDb.Options(connectionString), new SandboxOptions(), TimeProvider.System).EnsureDemoAsync(DemoId, Ct);
        await using var app = new WebApp(connectionString, settings: new Dictionary<string, string?>
        {
            ["Admin:Accounts:0"] = "dev:alice",
            ["Demo:TenantId"] = DemoId.ToString(),
            ["Operator:Name"] = "Example Operator",
            ["Operator:Email"] = "privacy@example.test",
            ["Links:About"] = "https://example.test/about",
        });
        var alice = await app.SignedInAsAsync("alice");
        Assert.Equal(HttpStatusCode.Redirect, (await alice.GetAsync("/lang/cs?return=/", Ct)).StatusCode);

        var payments = (await alice.GetFromJsonAsync<List<PaymentListItem>>("/api/payments", Ct))!;
        var queued = payments.First(p => p.State == PaymentState.NeedsReview).Id;
        string[] pages =
        [
            "/", "/queue", "/payments", "/payments?by=rule&sort=amount&dir=asc&q=a", "/how", "/privacy", "/admin", "/demo",
            $"/payments/{queued}/match", .. payments.Select(p => $"/payments/{p.Id}"),
        ];
        foreach (var page in pages)
        {
            var response = await alice.GetAsync(page, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{page}: {response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync(Ct);
            Assert.Contains("<html lang=\"cs\">", html, StringComparison.Ordinal);
        }

        Assert.Contains("Přehled", await alice.GetStringAsync("/", Ct), StringComparison.Ordinal);
        Assert.Empty(Text.Missing.Keys);

        // Back to English: the default for everyone else.
        await alice.GetAsync("/lang/en?return=/", Ct);
        Assert.Contains("<html lang=\"en\">", await alice.GetStringAsync("/", Ct), StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", await app.NewClient().GetStringAsync("/", Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/payments?state=matched", "/payments?state=matched")]
    [InlineData("//evil.example/x", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("", "/")]
    public async Task Switching_the_language_returns_to_a_local_page_only(string returnTo, string expected)
    {
        await using var app = new WebApp(await postgres.NewDatabaseAsync(Ct));

        var response = await app.NewClient().GetAsync($"/lang/cs?return={Uri.EscapeDataString(returnTo)}", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location!.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(Languages.CookieName + "=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.NotFound, (await app.NewClient().GetAsync("/lang/de", Ct)).StatusCode);
    }

    [Fact]
    public async Task Payments_list_searches_filters_sorts_and_pages_on_the_server()
    {
        await using var app = new WebApp(await postgres.NewDatabaseAsync(Ct), settings: new Dictionary<string, string?> { ["Sandboxes:StarterPayments"] = "40" });
        var alice = await app.SignedInAsAsync("alice");
        var all = (await alice.GetFromJsonAsync<List<PaymentListItem>>("/api/payments", Ct))!;
        Assert.Equal(40, all.Count);

        // 25 a page: two pages, the second holds the rest; a page past the end shows the last one.
        var first = await alice.GetStringAsync("/payments", Ct);
        Assert.Contains("aria-current=\"page\">1</span>", first, StringComparison.Ordinal);
        Assert.Contains("href=\"/payments?page=2\"", first, StringComparison.Ordinal);
        var second = await alice.GetStringAsync("/payments?page=2", Ct);
        var newestFirst = all.OrderByDescending(p => p.BookingDate).ThenByDescending(p => p.Id, StringComparer.Ordinal).ToList();
        Assert.Contains($">{newestFirst[25].Id}</a>", second, StringComparison.Ordinal);
        Assert.DoesNotContain($">{newestFirst[0].Id}</a>", second, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">2</span>", await alice.GetStringAsync("/payments?page=99", Ct), StringComparison.Ordinal);

        // Search by payer: only that payer's payments.
        var payer = all.First(p => p.CounterpartyName.Length > 6).CounterpartyName;
        var found = await alice.GetStringAsync($"/payments?q={Uri.EscapeDataString(payer)}", Ct);
        foreach (var p in all)
        {
            var listed = found.Contains($">{p.Id}</a>", StringComparison.Ordinal);
            var matches = p.CounterpartyName.Contains(payer, StringComparison.CurrentCultureIgnoreCase) || p.Message.Contains(payer, StringComparison.CurrentCultureIgnoreCase);
            Assert.Equal(matches, listed);
        }

        // Filter by decider and sort by amount, ascending.
        var byRule = PaymentQuery.From(null, "rule", null, "amount", "asc", null).Apply(all);
        Assert.All(byRule.Items, p => Assert.StartsWith("rule:", p.DecidedBy, StringComparison.Ordinal));
        Assert.Equal(byRule.Items.Select(p => p.Amount).Order(), byRule.Items.Select(p => p.Amount));
        var html = await alice.GetStringAsync("/payments?by=rule&sort=amount&dir=asc", Ct);
        var positions = byRule.Items.Select(p => html.IndexOf($">{p.Id}</a>", StringComparison.Ordinal)).ToList();
        Assert.All(positions, i => Assert.True(i > 0));
        Assert.Equal(positions.Order(), positions);

        // Unknown values are ignored, not errors; the search text comes back encoded.
        var odd = await alice.GetAsync("/payments?state=drop&by=x&sort=evil&dir=sideways&page=abc&q=%3Cscript%3E", Ct);
        Assert.Equal(HttpStatusCode.OK, odd.StatusCode);
        var oddHtml = await odd.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("<script>", oddHtml, StringComparison.Ordinal);
        Assert.Contains("value=\"&lt;script&gt;\"", oddHtml, StringComparison.Ordinal);
    }
}
