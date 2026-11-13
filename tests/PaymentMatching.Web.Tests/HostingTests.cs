using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using PaymentMatching.Data;
using PaymentMatching.Testing;

namespace PaymentMatching.Web.Tests;

/// <summary>What the app needs behind the Azure Container Apps ingress (ADR 0016).</summary>
public class HostingTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sign_in_survives_a_restart_because_the_key_ring_lives_in_the_database()
    {
        var database = await postgres.NewDatabaseAsync(Ct);
        string cookie;
        await using (var first = new WebApp(database))
        {
            var response = await first.NewClient().GetAsync("/dev-login?name=alice", Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        }

        await using (var db = new OperationsDbContext(OperationsDb.Options(database)))
        {
            Assert.NotEmpty(await db.DataProtectionKeys.ToListAsync(Ct));
        }

        // A new host (a new replica after scale-to-zero) with an empty container file system.
        await using var second = new WebApp(database);
        var client = second.NewClient();
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Theory]
    [InlineData(null, "http://localhost/signin-microsoft")]
    [InlineData("true", "https://localhost/signin-microsoft")]
    public async Task Behind_the_ingress_the_callback_uses_the_original_https_scheme(string? forwardedHeaders, string redirectUri)
    {
        var settings = new Dictionary<string, string?> { ["Auth:Microsoft:ClientId"] = "test-client-id", ["Auth:Microsoft:ClientSecret"] = "test-only" };
        if (forwardedHeaders is not null)
        {
            // ASPNETCORE_FORWARDEDHEADERS_ENABLED=true in the container app.
            settings["FORWARDEDHEADERS_ENABLED"] = forwardedHeaders;
        }

        await using var app = new WebApp(
            await postgres.NewDatabaseAsync(Ct),
            settings: settings,
            services: s => s.Configure<OpenIdConnectOptions>("microsoft", o =>
                o.Configuration = new OpenIdConnectConfiguration { AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize" }));

        // The ingress terminates TLS and forwards plain HTTP with X-Forwarded-Proto.
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");
        var response = await client.GetAsync("/login/microsoft", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal(redirectUri, query["redirect_uri"]);
    }
}
