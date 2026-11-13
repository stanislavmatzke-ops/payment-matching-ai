using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PaymentMatching.Data;
using PaymentMatching.Testing;

namespace PaymentMatching.Web.Tests;

public class SignInAndIsolationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<WebApp> App(IReadOnlyDictionary<string, string?>? settings = null) =>
        new(await postgres.NewDatabaseAsync(Ct), settings: settings);

    [Fact]
    public async Task Anonymous_visitor_sees_sign_in_and_no_data()
    {
        await using var app = await App();
        var client = app.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/payments", Ct)).StatusCode);
        var home = await client.GetStringAsync("/", Ct);
        Assert.Contains("/dev-login", home, StringComparison.Ordinal);
        Assert.DoesNotContain("/login/microsoft", home, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/login/microsoft", Ct)).StatusCode);
    }

    [Fact]
    public async Task Each_person_gets_an_own_sandbox_and_sees_only_its_data()
    {
        await using var app = await App();
        var alice = await app.SignedInAsAsync("alice");
        var bob = await app.SignedInAsAsync("bob");

        var aliceMe = await WebApp.MeAsync(alice);
        var bobMe = await WebApp.MeAsync(bob);
        Assert.NotEqual(aliceMe.TenantId, bobMe.TenantId);
        Assert.Equal(("alice", 40), (aliceMe.Name, aliceMe.Counts.Payments));
        Assert.Equal("dev:alice", aliceMe.AccountId);
        Assert.Equal(0, aliceMe.Counts.Payments - aliceMe.Counts.AutoMatched - aliceMe.Counts.HumanReview - aliceMe.Counts.NoMatch - aliceMe.Counts.WaitingForAi);

        // Both sandboxes use the same payment ids (P0001...): the API must show each person their own row.
        var alicePayments = (await alice.GetFromJsonAsync<List<PaymentListItem>>("/api/payments", Ct))!;
        await using (var db = PaymentsDb.ForTenant(app.ConnectionString, aliceMe.TenantId))
        {
            var own = db.Payments.OrderBy(p => p.Seq).Select(p => new { p.Id, p.Message, p.Amount }).ToList();
            Assert.Equal(own.Select(p => (p.Id, p.Message, p.Amount)), alicePayments.Select(p => (p.Id, p.Message, p.Amount)));
        }

        // Both start from the week's starter set (ADR 0017): the same content, but each in its own rows.
        var bobPayments = (await bob.GetFromJsonAsync<List<PaymentListItem>>("/api/payments", Ct))!;
        Assert.Equal(alicePayments.Select(p => (p.Id, p.Message, p.State)), bobPayments.Select(p => (p.Id, p.Message, p.State)));

        // Alice decides a payment of her queue: Bob's copy of the same payment stays in his queue.
        var queued = alicePayments.First(p => p.State == PaymentState.NeedsReview).Id;
        await using (var db = PaymentsDb.ForTenant(app.ConnectionString, aliceMe.TenantId))
        {
            Assert.Equal(ReviewOutcome.Done, (await new ReviewActions(db, TimeProvider.System).RejectAsync(queued, Ct)).Outcome);
        }

        Assert.NotEqual(PaymentState.NeedsReview, (await alice.GetFromJsonAsync<List<PaymentListItem>>("/api/payments", Ct))!.Single(p => p.Id == queued).State);
        Assert.Equal(PaymentState.NeedsReview, (await bob.GetFromJsonAsync<List<PaymentListItem>>("/api/payments", Ct))!.Single(p => p.Id == queued).State);

        var sameId = alicePayments[0].Id;
        var aliceDetail = (await alice.GetFromJsonAsync<PaymentDetailView>($"/api/payments/{sameId}", Ct))!;
        Assert.Equal(alicePayments[0].Message, aliceDetail.Payment.Message);

        // A payment that exists only in Bob's sandbox does not exist for Alice.
        await using (var db = PaymentsDb.ForTenant(app.ConnectionString, bobMe.TenantId))
        {
            db.Payments.Add(new PaymentRow
            {
                Id = "P9999",
                Seq = 99,
                Amount = 1,
                Currency = "CZK",
                CounterpartyIban = "",
                CounterpartyName = "Bob only",
                VariableSymbol = "",
                Message = "",
                RawLine = "P9999",
            });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/payments/P9999", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/payments/P9999", Ct)).StatusCode);
        Assert.DoesNotContain("Bob only", await alice.GetStringAsync("/api/payments", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_in_again_returns_to_the_same_sandbox()
    {
        await using var app = await App();

        var first = await WebApp.MeAsync(await app.SignedInAsAsync("alice"));
        var second = await WebApp.MeAsync(await app.SignedInAsAsync("alice"));

        Assert.Equal(first.TenantId, second.TenantId);
    }

    [Fact]
    public async Task A_deleted_sandbox_signs_its_cookie_out()
    {
        await using var app = await App();
        var alice = await app.SignedInAsAsync("alice");
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/me", Ct)).StatusCode);

        await new TenantAdmin(PaymentsDb.Options(app.ConnectionString)).DeleteInactiveAsync(DateTimeOffset.UtcNow.AddDays(400), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Daily_limit_sends_new_people_to_the_waiting_message()
    {
        await using var app = await App(new Dictionary<string, string?> { ["Sandboxes:MaxNewPerDay"] = "0" });
        var client = app.NewClient();

        var response = await client.GetAsync("/dev-login?name=alice", Ct);

        Assert.Equal("/?full=1", response.Headers.Location?.OriginalString);
        Assert.Contains("try again tomorrow", await client.GetStringAsync("/?full=1", Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await using var app = await App();
        var alice = await app.SignedInAsAsync("alice");

        var home = await alice.GetStringAsync("/", Ct);
        var token = System.Text.RegularExpressions.Regex.Match(home, "name=\"__af\" value=\"([^\"]+)\"").Groups[1].Value;

        var response = await alice.PostAsync("/logout", new FormUrlEncodedContent([KeyValuePair.Create("__af", token)]), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.GetAsync("/api/me", Ct)).StatusCode);
    }

    /// <summary>
    /// Visitors read, and act only on data that exists: confirm a stored proposal, reject, or pick
    /// invoices from a list (SPEC 9b). No endpoint takes free text, and every write needs sign-in and an
    /// anti-forgery token. A new write endpoint, or a new parameter on one, fails this test on purpose.
    /// </summary>
    [Fact]
    public async Task Visitors_can_only_read_no_endpoint_takes_text()
    {
        await using var app = await App();
        app.NewClient();

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var writes = endpoints
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Any(m => m != "GET") ?? true)
            .ToList();

        // Undo of an AI match (ADR 0012 amendment) is the one write added after phase 8; it takes the same form as a manual match.
        Assert.Equal(["/logout", "/payments/{id}/confirm", "/payments/{id}/match", "/payments/{id}/reject", "/payments/{id}/undo"], writes.Select(e => e.RoutePattern.RawText).Order(StringComparer.Ordinal));
        var allowed = new[] { typeof(string), typeof(ConfirmForm), typeof(MatchForm), typeof(ReviewActions), typeof(Microsoft.AspNetCore.Http.HttpContext) };
        Assert.All(writes, endpoint =>
        {
            Assert.True(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Antiforgery.IAntiforgeryMetadata>()?.RequiresValidation, endpoint.DisplayName);
            var handler = endpoint.Metadata.GetMetadata<System.Reflection.MethodInfo>()!;
            Assert.All(handler.GetParameters(), p =>
            {
                Assert.Contains(p.ParameterType, allowed);
                Assert.True(p.ParameterType != typeof(string) || p.Name == "id", $"{endpoint.DisplayName}: {p.Name}");
            });
        });

        // The forms carry ids and check codes only; the handlers validate their shape (see ReviewUiTests).
        Assert.Equal(["Ack", "Proposal"], typeof(ConfirmForm).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Ack", "Invoice"], typeof(MatchForm).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Pages_send_security_headers_and_privacy_is_public()
    {
        await using var app = await App();
        var response = await app.NewClient().GetAsync("/privacy", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("30 days", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Dev_login_cannot_be_enabled_outside_development()
    {
        await using var app = new WebApp(await postgres.NewDatabaseAsync(Ct), environment: "Production");

        var error = Assert.ThrowsAny<Exception>(() => app.NewClient());
        Assert.Contains("Development", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_without_dev_login_has_no_dev_endpoint()
    {
        await using var app = new WebApp(await postgres.NewDatabaseAsync(Ct), environment: "Production", settings: new Dictionary<string, string?> { ["Auth:DevLogin"] = "false" });

        Assert.Equal(HttpStatusCode.NotFound, (await app.NewClient().GetAsync("/dev-login?name=alice", Ct)).StatusCode);
    }
}

/// <summary>OIDC wiring, checked without contacting Microsoft or Google (no secrets, no network).</summary>
public class IdentityProviderTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, string> Providers => new()
    {
        { "microsoft", "https://login.microsoftonline.com/common/v2.0", "https://login.microsoftonline.com/common/oauth2/v2.0/authorize" },
        { "google", "https://accounts.google.com", "https://accounts.google.com/o/oauth2/v2/auth" },
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Sign_in_redirects_to_the_provider_with_code_flow_and_pkce(string provider, string authority, string authorizeEndpoint)
    {
        var name = provider == "microsoft" ? "Microsoft" : "Google";
        await using var app = new WebApp(
            await postgres.NewDatabaseAsync(Ct),
            settings: new Dictionary<string, string?> { [$"Auth:{name}:ClientId"] = "test-client-id", [$"Auth:{name}:ClientSecret"] = "test-only" },
            services: s => s.Configure<OpenIdConnectOptions>(provider, o =>
                o.Configuration = new OpenIdConnectConfiguration { AuthorizationEndpoint = authorizeEndpoint, Issuer = authority }));
        var client = app.NewClient();

        Assert.Contains($"/login/{provider}", await client.GetStringAsync("/", Ct), StringComparison.Ordinal);
        var response = await client.GetAsync($"/login/{provider}", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(authorizeEndpoint + "?", location.AbsoluteUri, StringComparison.Ordinal);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("test-client-id", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("openid profile email", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal($"https://localhost/signin-{provider}", query["redirect_uri"]);

        var options = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(provider);
        Assert.Equal(authority, options.Authority);
        Assert.False(options.SaveTokens);
        Assert.Equal(Authentication.Microsoft == provider, options.TokenValidationParameters.IssuerValidator is not null);
    }

    [Fact]
    public void Microsoft_issuer_must_belong_to_the_tokens_own_directory()
    {
        var tid = Guid.NewGuid();
        var token = Token(tid.ToString());
        var parameters = new TokenValidationParameters();

        Assert.Equal($"https://login.microsoftonline.com/{tid}/v2.0", MicrosoftIssuer.Validate($"https://login.microsoftonline.com/{tid}/v2.0", token, parameters));
        Assert.Throws<SecurityTokenInvalidIssuerException>(() => MicrosoftIssuer.Validate($"https://login.microsoftonline.com/{Guid.NewGuid()}/v2.0", token, parameters));
        Assert.Throws<SecurityTokenInvalidIssuerException>(() => MicrosoftIssuer.Validate("https://evil.example/v2.0", token, parameters));
        Assert.Throws<SecurityTokenInvalidIssuerException>(() => MicrosoftIssuer.Validate("https://login.microsoftonline.com/{tenantid}/v2.0", Token("{tenantid}"), parameters));
    }

    private static JsonWebToken Token(string tid)
    {
        static string Part(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));
        return new JsonWebToken($"{Part("{\"alg\":\"none\"}")}.{Part($"{{\"tid\":\"{tid}\"}}")}.");
    }
}
