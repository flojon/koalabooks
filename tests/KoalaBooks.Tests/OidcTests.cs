using KoalaBooks.Domain;
using KoalaBooks.Domain.Auth;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KoalaBooks.Tests;

internal static class OidcTestHelpers
{
    public static string ExtractAntiforgeryToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
}

// Reproduces a production incident where dashboard.koalasoft.se returned 500: the token endpoint
// (Token.cshtml.cs) only ever implemented the "password" grant, so the Aspire dashboard's real
// login flow - which redeems its authorization code via the "authorization_code" grant - was
// unconditionally Forbid()'d and surfaced to the dashboard as OpenIddict's generic invalid_grant.
public class OidcAuthorizationCodeGrantTests
{
    [Fact]
    public async Task TokenEndpoint_RedeemsAuthorizationCode_ReturnsAccessToken()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            await using var factory = new WebApiFactory(connStr);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            const string email = "dashboard-user@test.com";
            const string password = "ValidPass123!";
            const string clientSecret = "aspire-dashboard-dev-secret";
            var redirectUri = new Uri("http://localhost:18888/signin-oidc");

            using (var scope = factory.Services.CreateScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var created = await userManager.CreateAsync(
                    new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true }, password);
                Assert.True(created.Succeeded);

                await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, redirectUri, clientSecret);
            }

            var loginPage = await client.GetAsync("/account/login");
            var antiforgeryToken = OidcTestHelpers.ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

            var loginResponse = await client.PostAsync("/account/login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Email"] = email,
                    ["Password"] = password,
                    ["__RequestVerificationToken"] = antiforgeryToken,
                }));
            Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

            var authorizeResponse = await client.GetAsync(
                $"/connect/authorize?client_id=aspire-dashboard&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}&scope=openid%20profile");
            Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);

            var code = QueryHelpers.ParseQuery(authorizeResponse.Headers.Location!.Query)["code"].ToString();
            Assert.False(string.IsNullOrEmpty(code));

            var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri.ToString(),
                    ["client_id"] = "aspire-dashboard",
                    ["client_secret"] = clientSecret,
                }));

            var body = await tokenResponse.Content.ReadAsStringAsync();
            Assert.True(tokenResponse.IsSuccessStatusCode, body);
            Assert.Contains("access_token", body);
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }
}

// Reproduces a production incident where the dashboard's authorize request (scope=openid profile)
// was rejected with invalid_scope (OpenIddict ID2052) because "profile" was never registered as a
// server-level scope via RegisterScopes, even though the client had the scp:profile permission.
public class OidcAuthorizeScopeTests
{
    [Fact]
    public async Task Authorize_WithOpenIdProfileScope_IsAcceptedByOpenIddict()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            await using var factory = new WebApiFactory(connStr);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Startup seeding (AspireDashboardSeeder) runs asynchronously before app.Run(); re-run it
            // here (it's an idempotent upsert, same as on every real restart) so the test isn't flaky
            // about whether that startup task has completed by the time CreateClient() returns.
            using (var scope = factory.Services.CreateScope())
            {
                await AspireDashboardSeeder.SeedAsync(
                    scope.ServiceProvider,
                    new Uri("http://localhost:18888/signin-oidc"),
                    "aspire-dashboard-dev-secret");
            }

            var redirectUri = Uri.EscapeDataString("http://localhost:18888/signin-oidc");
            var response = await client.GetAsync(
                $"/connect/authorize?client_id=aspire-dashboard&response_type=code&redirect_uri={redirectUri}&scope=openid%20profile");

            var location = response.Headers.Location?.ToString() ?? "";
            var body = await response.Content.ReadAsStringAsync();

            // A rejected request returns invalid_scope directly (or redirects with an error query
            // string). An accepted-but-unauthenticated request is passed through to the app, which
            // challenges the user by redirecting to the login page instead.
            Assert.DoesNotContain("invalid_scope", body);
            Assert.DoesNotContain("error=invalid_scope", location);
            Assert.Contains("/account/login", location);
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }
}

public class OidcClientSeedingTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbName;
    private static readonly Uri DashboardRedirectUri = new("http://localhost:18888/");

    public OidcClientSeedingTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUser>(new LocalCurrentUser());
        services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(connStr));
        services.AddOpenIddict()
            .AddCore(opts => opts.UseEntityFrameworkCore().UseDbContext<AppDbContext>());

        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task SeedAsync_CreatesAspireDashboardClient()
    {
        using var scope = _sp.CreateScope();
        await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, DashboardRedirectUri, "test-secret");

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = await manager.FindByClientIdAsync("aspire-dashboard");

        Assert.NotNull(app);
    }

    [Fact]
    public async Task SeedAsync_ClientHasExpectedPermissions()
    {
        using var scope = _sp.CreateScope();
        await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, DashboardRedirectUri, "test-secret");

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = (await manager.FindByClientIdAsync("aspire-dashboard"))!;
        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, app);

        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.RefreshToken, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Endpoints.Authorization, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Endpoints.Token, descriptor.Permissions);
        Assert.Contains(
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
            descriptor.Permissions);
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent()
    {
        using (var scope = _sp.CreateScope())
            await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, DashboardRedirectUri, "test-secret");

        using (var scope = _sp.CreateScope())
            await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, DashboardRedirectUri, "test-secret");

        using var verifyScope = _sp.CreateScope();
        var manager = verifyScope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var count = await manager.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SeedAsync_RegistersRedirectUri()
    {
        using var scope = _sp.CreateScope();
        await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, DashboardRedirectUri, "test-secret");

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = (await manager.FindByClientIdAsync("aspire-dashboard"))!;
        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, app);

        Assert.Contains(DashboardRedirectUri, descriptor.RedirectUris);
        Assert.Single(descriptor.RedirectUris);
    }

    [Fact]
    public async Task SeedAsync_UpdatesExistingClient()
    {
        var originalUri = new Uri("http://localhost:18888/");
        var updatedUri = new Uri("http://localhost:19999/");

        using (var scope = _sp.CreateScope())
            await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, originalUri, "secret-v1");

        using (var scope = _sp.CreateScope())
            await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, updatedUri, "secret-v2");

        using var verifyScope = _sp.CreateScope();
        var manager = verifyScope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = (await manager.FindByClientIdAsync("aspire-dashboard"))!;
        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, app);

        Assert.Equal(1, await manager.CountAsync());
        Assert.Contains(updatedUri, descriptor.RedirectUris);
        Assert.DoesNotContain(originalUri, descriptor.RedirectUris);
    }

    public void Dispose()
    {
        _sp.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }
}

public class WasmClientSeedingTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbName;
    private static readonly Uri BaseUri = new("https://books.koalasoft.se/");

    public WasmClientSeedingTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUser>(new LocalCurrentUser());
        services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(connStr));
        services.AddOpenIddict()
            .AddCore(opts => opts.UseEntityFrameworkCore().UseDbContext<AppDbContext>());

        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task SeedAsync_CreatesPublicClientWithAuthorizationCodeAndPkce()
    {
        using var scope = _sp.CreateScope();
        await WasmClientSeeder.SeedAsync(scope.ServiceProvider, BaseUri);

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = await manager.FindByClientIdAsync(WasmClientSeeder.ClientId);
        Assert.NotNull(app);

        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, app);

        Assert.Equal(OpenIddictConstants.ClientTypes.Public, descriptor.ClientType);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.ResponseTypes.Code, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Endpoints.Authorization, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Endpoints.Token, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange, descriptor.Requirements);
        Assert.DoesNotContain(
            OpenIddictConstants.Permissions.Prefixes.GrantType + "urn:koalabooks:grant-type:cookie",
            descriptor.Permissions);
    }

    [Fact]
    public async Task SeedAsync_RegistersLoginAndLogoutCallbackUris()
    {
        using var scope = _sp.CreateScope();
        await WasmClientSeeder.SeedAsync(scope.ServiceProvider, BaseUri);

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = (await manager.FindByClientIdAsync(WasmClientSeeder.ClientId))!;
        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, app);

        Assert.Contains(new Uri(BaseUri, "authentication/login-callback"), descriptor.RedirectUris);
        Assert.Contains(new Uri(BaseUri, "authentication/logout-callback"), descriptor.PostLogoutRedirectUris);
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent()
    {
        using (var scope = _sp.CreateScope())
            await WasmClientSeeder.SeedAsync(scope.ServiceProvider, BaseUri);

        using (var scope = _sp.CreateScope())
            await WasmClientSeeder.SeedAsync(scope.ServiceProvider, BaseUri);

        using var verifyScope = _sp.CreateScope();
        var manager = verifyScope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        Assert.Equal(1, await manager.CountAsync());
    }

    public void Dispose()
    {
        _sp.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }
}

// #292: proves the WASM client can bridge its ambient Identity cookie session to a bearer
// token via the cookie grant, without ever going through AddOidcAuthentication()'s
// RemoteAuthenticationService (whose DI-slot conflict with AddAuthenticationStateDeserialization()
// is what #292 is about). The minted token both authenticates against the API and carries org_id
// for tenant scoping, same as the password/authorization_code grants.
public class OidcCookieGrantForWasmClientTests
{
    [Fact]
    public async Task CookieGrant_ForWasmClient_ReturnsAccessTokenWithOrgId()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            await using var factory = new WebApiFactory(connStr);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            const string email = "wasm-user@test.com";
            const string password = "ValidPass123!";
            int orgId;

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var org = new Organisation { Name = "Wasm Test Org", Slug = "wasm-test", LegalForm = LegalForm.Aktiebolag };
                db.Organisations.Add(org);
                await db.SaveChangesAsync();
                orgId = org.Id;

                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var created = await userManager.CreateAsync(
                    new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, OrganisationId = orgId },
                    password);
                Assert.True(created.Succeeded);

                await WasmClientSeeder.SeedAsync(scope.ServiceProvider, new Uri("http://localhost:5000/"));
            }

            var loginPage = await client.GetAsync("/account/login");
            var antiforgeryToken = OidcTestHelpers.ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

            var loginResponse = await client.PostAsync("/account/login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Email"] = email,
                    ["Password"] = password,
                    ["__RequestVerificationToken"] = antiforgeryToken,
                }));
            Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = WasmCookieBridge.GrantType,
                    ["client_id"] = WasmClientSeeder.ClientId,
                    ["scope"] = "email profile",
                })
            };
            tokenRequest.Headers.Add(WasmCookieBridge.CsrfHeaderName, WasmCookieBridge.CsrfHeaderValue);

            var tokenResponse = await client.SendAsync(tokenRequest);
            var body = await tokenResponse.Content.ReadAsStringAsync();
            Assert.True(tokenResponse.IsSuccessStatusCode, body);

            var json = JsonSerializer.Deserialize<JsonElement>(body);
            var accessToken = json.GetProperty("access_token").GetString()!;
            var payload = accessToken.Split('.')[1];
            var claimsJson = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(payload));
            var claims = JsonSerializer.Deserialize<JsonElement>(claimsJson);

            Assert.Equal(orgId.ToString(), claims.GetProperty("org_id").GetString());
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }

    [Fact]
    public async Task CookieGrant_WithoutCsrfHeader_IsRejected()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            await using var factory = new WebApiFactory(connStr);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            const string email = "wasm-user-2@test.com";
            const string password = "ValidPass123!";

            using (var scope = factory.Services.CreateScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var created = await userManager.CreateAsync(
                    new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true }, password);
                Assert.True(created.Succeeded);

                await WasmClientSeeder.SeedAsync(scope.ServiceProvider, new Uri("http://localhost:5000/"));
            }

            var loginPage = await client.GetAsync("/account/login");
            var antiforgeryToken = OidcTestHelpers.ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

            var loginResponse = await client.PostAsync("/account/login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Email"] = email,
                    ["Password"] = password,
                    ["__RequestVerificationToken"] = antiforgeryToken,
                }));
            Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

            // No X-KoalaBooks-Csrf header this time.
            var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = WasmCookieBridge.GrantType,
                    ["client_id"] = WasmClientSeeder.ClientId,
                    ["scope"] = "email profile",
                }));

            var body = await tokenResponse.Content.ReadAsStringAsync();
            Assert.False(tokenResponse.IsSuccessStatusCode, body);
            Assert.Contains("invalid_request", body);
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }
}

// Verifies RP-initiated logout actually terminates the server-side session: without this,
// the SPA's local sign-out would leave the Identity cookie valid and a silent-renew would
// re-authenticate the user without them noticing.
public class OidcLogoutEndpointTests
{
    [Fact]
    public async Task Logout_SignsOutCookie_SubsequentAuthorizeChallengesLogin()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            await using var factory = new WebApiFactory(connStr);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            const string email = "logout-user@test.com";
            const string password = "ValidPass123!";
            var baseUri = new Uri("https://books.koalasoft.se/");
            var redirectUri = new Uri(baseUri, "authentication/login-callback");
            var postLogoutUri = new Uri(baseUri, "authentication/logout-callback");

            using (var scope = factory.Services.CreateScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var created = await userManager.CreateAsync(
                    new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true }, password);
                Assert.True(created.Succeeded);

                await WasmClientSeeder.SeedAsync(scope.ServiceProvider, baseUri);
            }

            var loginPage = await client.GetAsync("/account/login");
            var antiforgeryToken = OidcTestHelpers.ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

            var loginResponse = await client.PostAsync("/account/login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Email"] = email,
                    ["Password"] = password,
                    ["__RequestVerificationToken"] = antiforgeryToken,
                }));
            Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

            var authorizeUrl = $"/connect/authorize?client_id={WasmClientSeeder.ClientId}&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}&scope=openid%20profile" +
                "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256";

            var authorizeBeforeLogout = await client.GetAsync(authorizeUrl);
            Assert.Equal(HttpStatusCode.Redirect, authorizeBeforeLogout.StatusCode);
            Assert.Contains("code=", authorizeBeforeLogout.Headers.Location!.Query);

            var logoutResponse = await client.GetAsync(
                $"/connect/logout?client_id={WasmClientSeeder.ClientId}" +
                $"&post_logout_redirect_uri={Uri.EscapeDataString(postLogoutUri.ToString())}");
            Assert.Equal(HttpStatusCode.Redirect, logoutResponse.StatusCode);
            Assert.StartsWith(postLogoutUri.ToString(), logoutResponse.Headers.Location!.ToString());

            var authorizeAfterLogout = await client.GetAsync(authorizeUrl);
            Assert.Equal(HttpStatusCode.Redirect, authorizeAfterLogout.StatusCode);
            Assert.Contains("/account/login", authorizeAfterLogout.Headers.Location!.ToString());
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }
}
