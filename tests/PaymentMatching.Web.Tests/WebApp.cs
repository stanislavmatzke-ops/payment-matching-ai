using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PaymentMatching.Testing;
using PaymentMatching.Web;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace PaymentMatching.Web.Tests;

/// <summary>
/// The web app in memory over a fresh test database. Identity providers are off unless a test
/// configures them; sign-in goes through the real dev-login cookie flow (Development only).
/// </summary>
internal sealed class WebApp(
    string connectionString,
    string environment = "Development",
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Program>
{
    public string ConnectionString => connectionString;

    public HttpClient NewClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    /// <summary>A browser session signed in as <paramref name="name"/>.</summary>
    public async Task<HttpClient> SignedInAsAsync(string name)
    {
        var client = NewClient();
        var response = await client.GetAsync($"/dev-login?name={name}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        return client;
    }

    public static async Task<MeView> MeAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<MeView>("/api/me", TestContext.Current.CancellationToken))!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Payments", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Auth:DevLogin", "true");

        // Ignore any real client ids in the developer's user-secrets.
        foreach (var key in new[] { "Auth:Microsoft:ClientId", "Auth:Microsoft:ClientSecret", "Auth:Google:ClientId", "Auth:Google:ClientSecret" })
        {
            builder.UseSetting(key, "");
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        if (services is not null)
        {
            builder.ConfigureTestServices(services);
        }
    }
}
