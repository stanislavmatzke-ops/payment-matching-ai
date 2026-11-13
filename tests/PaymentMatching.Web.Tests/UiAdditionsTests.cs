using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Testing;

namespace PaymentMatching.Web.Tests;

/// <summary>The invoice panel in the payment detail, the admin tab and the sign-in buttons.</summary>
public class UiAdditionsTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Payment_detail_shows_the_named_invoices_of_its_own_sandbox_and_the_difference()
    {
        await using var app = new WebApp(await postgres.NewDatabaseAsync(Ct));
        var alice = await app.SignedInAsAsync("alice");
        var bob = await app.SignedInAsAsync("bob");
        var bobMe = await WebApp.MeAsync(bob);

        PaymentDetailView? detail = null;
        foreach (var item in (await alice.GetFromJsonAsync<List<PaymentListItem>>("/api/payments", Ct))!)
        {
            detail = await alice.GetFromJsonAsync<PaymentDetailView>($"/api/payments/{item.Id}", Ct);
            if (detail!.Invoices.Count > 0 && detail.Comparisons.Any(c => c.Difference is not null))
            {
                break;
            }
        }

        Assert.NotNull(detail);
        var comparison = detail.Comparisons.First(c => c.Difference is not null);
        var named = detail.Invoices.Where(i => comparison.InvoiceNumbers.Contains(i.Number)).ToList();
        Assert.Equal(named.Sum(i => i.Amount), comparison.Total);
        Assert.Equal(detail.Payment.Amount - comparison.Total, comparison.Difference);

        // Bob's sandbox has an invoice with the same number: changing his never shows up in Alice's panel.
        var number = named[0].Number;
        await using (var db = PaymentsDb.ForTenant(app.ConnectionString, bobMe.TenantId))
        {
            await db.Invoices.Where(i => i.Number == number).ExecuteUpdateAsync(s => s.SetProperty(i => i.Amount, 987654.32m), Ct);
        }

        var again = (await alice.GetFromJsonAsync<PaymentDetailView>($"/api/payments/{detail.Payment.Id}", Ct))!;
        Assert.Equal(named[0].Amount, again.Invoices.Single(i => i.Number == number).Amount);

        var html = await alice.GetStringAsync($"/payments/{detail.Payment.Id}", Ct);
        Assert.Contains($"<strong>{number}</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("987,654.32", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_an_admin_sees_the_admin_tab()
    {
        await using var app = new WebApp(await postgres.NewDatabaseAsync(Ct), settings: new Dictionary<string, string?> { ["Admin:Accounts:0"] = "dev:boss" });
        var alice = await app.SignedInAsAsync("alice");
        var boss = await app.SignedInAsAsync("boss");

        Assert.DoesNotContain("href=\"/admin\"", await alice.GetStringAsync("/", Ct), StringComparison.Ordinal);
        Assert.Contains("href=\"/admin\"", await boss.GetStringAsync("/", Ct), StringComparison.Ordinal);

        var admin = await boss.GetStringAsync("/admin", Ct);
        Assert.Contains("<a href=\"/admin\" class=\"active\">", admin, StringComparison.Ordinal);
        Assert.Contains("action=\"/logout\"", admin, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Privacy_page_names_the_data_controller_and_where_to_ask_for_deletion()
    {
        await using var app = new WebApp(
            await postgres.NewDatabaseAsync(Ct),
            settings: new Dictionary<string, string?> { ["Operator:Name"] = "Example Operator", ["Operator:Email"] = "privacy@example.test" });

        var page = await app.NewClient().GetStringAsync("/privacy", Ct);

        Assert.Contains("The data controller is Example Operator.", page, StringComparison.Ordinal);
        Assert.Contains("<a href=\"mailto:privacy@example.test\">privacy@example.test</a>", page, StringComparison.Ordinal);
        Assert.Contains("deleted right away", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_page_offers_the_providers_as_buttons_and_the_demo()
    {
        await using var app = new WebApp(
            await postgres.NewDatabaseAsync(Ct),
            settings: new Dictionary<string, string?>
            {
                ["Auth:Microsoft:ClientId"] = "test-client-id",
                ["Auth:Microsoft:ClientSecret"] = "test-only",
                ["Auth:Google:ClientId"] = "test-client-id",
                ["Auth:Google:ClientSecret"] = "test-only",
                ["Demo:TenantId"] = Guid.NewGuid().ToString(),
            });

        var page = await app.NewClient().GetStringAsync("/", Ct);

        Assert.Contains("<a class=\"provider-button\" href=\"/login/microsoft\">", page, StringComparison.Ordinal);
        Assert.Contains("Continue with Microsoft", page, StringComparison.Ordinal);
        Assert.Contains("<a class=\"provider-button\" href=\"/login/google\">", page, StringComparison.Ordinal);
        Assert.Contains("Continue with Google", page, StringComparison.Ordinal);
        Assert.Contains("class=\"provider-logo\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", page, StringComparison.Ordinal);
    }
}
