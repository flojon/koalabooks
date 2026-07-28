# WASM standalone foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn `KoalaBooks.Web` into a pure API/auth-server backend and `KoalaBooks.Client` into a standalone `InteractiveWebAssembly` SPA, replacing the custom cookie-bridge OAuth grant with standard authorization-code + PKCE, served same-origin behind Caddy.

**Architecture:** Delete all Blazor Components hosting from `KoalaBooks.Web` (no more `MapRazorComponents`, no `App.razor`); `KoalaBooks.Client` becomes a self-hosted Blazor WASM app with its own `wwwroot/index.html` and root components, authenticating via `AddOidcAuthentication()` against the same-origin OpenIddict authority. Caddy splits traffic by path: `/api/*` and `/connect/*` reverse-proxy to the `KoalaBooks.Web` container, everything else is served as static files (SPA fallback to `index.html`) from a new `KoalaBooks.Client` static-file container.

**Tech Stack:** .NET 10 / ASP.NET Core, Blazor WebAssembly (`Microsoft.AspNetCore.Components.WebAssembly.Authentication`), OpenIddict 7.6, MudBlazor 9.7, Caddy 2, Docker Compose, xUnit + Testcontainers (`WebApiFactory`).

## Global Constraints

- Same-origin only: no CORS is introduced anywhere (per design spec, "CORS: none needed").
- No refresh-token grant for the WASM client — session ends when the tab/browser closes (explicit non-goal to extend this in this sub-project).
- `/review` (the only WASM-rendered page today) must keep working end-to-end after every task that touches auth or hosting.
- `dotnet build` must stay at 0 warnings/errors after every task.
- Don't touch the Aspire-dashboard OIDC client (`AspireDashboardSeeder`), the `password` grant, or any of the 8 existing client-side API services beyond what's needed to keep them compiling — resource/page conversion is out of scope (sub-project 4).

---

## Task 1: `WasmClientSeeder` — switch to Authorization Code + PKCE

**Files:**
- Modify: `src/KoalaBooks.Infrastructure/Services/WasmClientSeeder.cs`
- Modify: `src/KoalaBooks.Web/Program.cs:317-323` (call site, needs a `Uri` argument now)
- Test: `tests/KoalaBooks.Tests/OidcTests.cs` (the `WasmClientSeedingTests` class, lines 258-320)

**Interfaces:**
- Produces: `WasmClientSeeder.SeedAsync(IServiceProvider services, Uri baseUri)` — signature changes from `SeedAsync(IServiceProvider services)`. `baseUri` is the app's own public origin (e.g. `https://books.koalasoft.se/`); the seeder appends `authentication/login-callback` and `authentication/logout-callback` to it for the registered redirect URIs. Mirrors the existing `AspireDashboardSeeder.SeedAsync(services, redirectUri, clientSecret)` pattern.

- [ ] **Step 1: Write the failing tests**

Replace the two tests in `WasmClientSeedingTests` (`tests/KoalaBooks.Tests/OidcTests.cs:258-320`) with:

```csharp
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
```

Note: this hardcodes the literal grant-type string `"urn:koalabooks:grant-type:cookie"` in the `DoesNotContain` assertion rather than referencing `WasmCookieBridge.GrantType`, because Task 3 deletes that class — this test must still compile after Task 3 lands.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~WasmClientSeedingTests"`
Expected: FAIL — compile error, `WasmClientSeeder.SeedAsync` doesn't accept a `Uri` argument yet.

- [ ] **Step 3: Rewrite `WasmClientSeeder`**

Replace `src/KoalaBooks.Infrastructure/Services/WasmClientSeeder.cs` in full:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace KoalaBooks.Infrastructure.Services;

public static class WasmClientSeeder
{
    public const string ClientId = "koalabooks-wasm";

    public static async Task SeedAsync(IServiceProvider services, Uri baseUri)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        var logger = services.GetRequiredService<ILoggerFactory>()
                             .CreateLogger(typeof(WasmClientSeeder));

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            DisplayName = "KoalaBooks WASM client",
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                OpenIddictConstants.Permissions.Scopes.Email,
                OpenIddictConstants.Permissions.Scopes.Profile,
            },
            Requirements =
            {
                OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
            },
        };
        descriptor.RedirectUris.Add(new Uri(baseUri, "authentication/login-callback"));
        descriptor.PostLogoutRedirectUris.Add(new Uri(baseUri, "authentication/logout-callback"));

        var existing = await manager.FindByClientIdAsync(ClientId).ConfigureAwait(false);
        if (existing is null)
        {
            await manager.CreateAsync(descriptor).ConfigureAwait(false);
            logger.LogInformation("Created OpenIddict client '{ClientId}'", ClientId);
        }
        else
        {
            await manager.UpdateAsync(existing, descriptor).ConfigureAwait(false);
            logger.LogInformation("Updated OpenIddict client '{ClientId}'", ClientId);
        }
    }
}
```

- [ ] **Step 4: Update the call site in `Program.cs`**

In `src/KoalaBooks.Web/Program.cs`, replace line 323:

```csharp
        await WasmClientSeeder.SeedAsync(scope.ServiceProvider);
```

with:

```csharp
        var publicOrigin = builder.Configuration["PublicOrigin"] ?? "http://localhost:5000";
        await WasmClientSeeder.SeedAsync(scope.ServiceProvider, new Uri(publicOrigin));
```

This will not compile yet — `WasmCookieBridge` is still referenced elsewhere in `Program.cs` (line 144, `AllowCustomFlow`) and will keep compiling fine until Task 3 removes it. This step only changes the `WasmClientSeeder` call.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~WasmClientSeedingTests"`
Expected: PASS (3 tests)

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Services/WasmClientSeeder.cs src/KoalaBooks.Web/Program.cs tests/KoalaBooks.Tests/OidcTests.cs
git commit -m "Switch koalabooks-wasm OpenIddict client to authorization code + PKCE"
```

---

## Task 2: Add `/connect/logout` RP-initiated logout endpoint

**Why this task exists:** the design spec registers `PostLogoutRedirectUris` for the WASM client but never adds a server-side logout endpoint for OpenIddict to complete RP-initiated sign-out. Without one, the SPA's local logout only clears its own in-memory/sessionStorage state — the ambient ASP.NET Identity cookie stays valid, so the next silent-renew (`prompt=none`) silently re-authenticates the user. The design's own verification plan requires "logout → session cleared → protected route redirects to login again," which needs this endpoint to actually be true.

**Files:**
- Modify: `src/KoalaBooks.Web/Program.cs:137-174` (add `SetLogoutEndpointUris` + `EnableLogoutEndpointPassthrough`)
- Create: `src/KoalaBooks.Web/Pages/Connect/Logout.cshtml`
- Create: `src/KoalaBooks.Web/Pages/Connect/Logout.cshtml.cs`
- Test: `tests/KoalaBooks.Tests/OidcTests.cs` (new test class)

**Interfaces:**
- Consumes: `OpenIddictServerAspNetCoreDefaults.AuthenticationScheme` (already used by `Authorize.cshtml.cs`/`Token.cshtml.cs`), `IdentityConstants.ApplicationScheme`.
- Produces: `GET/POST /connect/logout` — signs out the Identity cookie, then completes OpenIddict's RP-initiated logout (redirects to the caller's registered `post_logout_redirect_uri`).

- [ ] **Step 1: Write the failing test**

Add to `tests/KoalaBooks.Tests/OidcTests.cs` (new top-level class, alongside the existing ones):

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~OidcLogoutEndpointTests"`
Expected: FAIL — `/connect/logout` doesn't exist yet (OpenIddict rejects the unrecognized endpoint, or the first `authorizeBeforeLogout` assertion fails because PKCE support isn't wired into the server pipeline's redemption test path — either way, it fails before the endpoint exists).

- [ ] **Step 3: Add the logout endpoint to the OpenIddict server config**

In `src/KoalaBooks.Web/Program.cs`, inside the `AddServer(options => { ... })` block (currently lines 137-174), change:

```csharp
        options.SetAuthorizationEndpointUris("/connect/authorize");
        options.SetTokenEndpointUris("/connect/token");
```

to:

```csharp
        options.SetAuthorizationEndpointUris("/connect/authorize");
        options.SetTokenEndpointUris("/connect/token");
        options.SetLogoutEndpointUris("/connect/logout");
```

and change:

```csharp
        options.UseAspNetCore()
               .EnableAuthorizationEndpointPassthrough()
               .EnableTokenEndpointPassthrough()
               .DisableTransportSecurityRequirement();
```

to:

```csharp
        options.UseAspNetCore()
               .EnableAuthorizationEndpointPassthrough()
               .EnableTokenEndpointPassthrough()
               .EnableLogoutEndpointPassthrough()
               .DisableTransportSecurityRequirement();
```

- [ ] **Step 4: Create the Logout Razor Page**

Create `src/KoalaBooks.Web/Pages/Connect/Logout.cshtml`:

```
@page "/connect/logout"
@model KoalaBooks.Web.Pages.Connect.LogoutModel
```

Create `src/KoalaBooks.Web/Pages/Connect/Logout.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenIddict.Server.AspNetCore;

namespace KoalaBooks.Web.Pages.Connect;

// RP-initiated logout: signs out the ASP.NET Identity cookie, then lets OpenIddict's
// middleware complete the redirect to the caller's registered post_logout_redirect_uri.
[IgnoreAntiforgeryToken]
public class LogoutModel : PageModel
{
    private readonly SignInManager<KoalaBooks.Infrastructure.Data.ApplicationUser> _signInManager;

    public LogoutModel(SignInManager<KoalaBooks.Infrastructure.Data.ApplicationUser> signInManager)
    {
        _signInManager = signInManager;
    }

    public Task<IActionResult> OnGetAsync() => HandleAsync();
    public Task<IActionResult> OnPostAsync() => HandleAsync();

    private async Task<IActionResult> HandleAsync()
    {
        await _signInManager.SignOutAsync();

        return SignOut(
            authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            properties: new AuthenticationProperties());
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~OidcLogoutEndpointTests|FullyQualifiedName~WasmClientSeedingTests"`
Expected: PASS

- [ ] **Step 6: Run the full OidcTests suite to check for regressions**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~OidcTests|FullyQualifiedName~Oidc"`
Expected: PASS (existing dashboard/password-grant tests unaffected)

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Web/Program.cs src/KoalaBooks.Web/Pages/Connect/Logout.cshtml src/KoalaBooks.Web/Pages/Connect/Logout.cshtml.cs tests/KoalaBooks.Tests/OidcTests.cs
git commit -m "Add /connect/logout RP-initiated logout endpoint for the WASM client"
```

---

## Task 3: Remove the cookie-bridge grant (server side) and rewrite its tests

**Files:**
- Modify: `src/KoalaBooks.Web/Program.cs:1-4,141-144` (drop `AllowCustomFlow`, unused usings)
- Modify: `src/KoalaBooks.Web/Pages/Connect/Token.cshtml.cs` (drop the cookie-grant branch and `HandleCookieGrantAsync`)
- Delete: `src/KoalaBooks.Domain/Auth/WasmCookieBridge.cs`
- Modify: `tests/KoalaBooks.Tests/OidcTests.cs` (replace `OidcCookieGrantForWasmClientTests`, lines 322-452, with an authorization-code + PKCE round-trip test)

**Interfaces:**
- Produces: `OidcTestHelpers` gets two new static helpers used by the new test — `GeneratePkcePair()` returning `(string Verifier, string Challenge)`.

- [ ] **Step 1: Write the failing test**

Replace the entire `OidcCookieGrantForWasmClientTests` class (`tests/KoalaBooks.Tests/OidcTests.cs:322-452`, including its leading comment) with:

```csharp
// Proves the WASM client's real flow end-to-end: authorization_code + PKCE against the
// ambient Identity cookie's login page, redeeming the code without a client_secret (public
// client). Replaces the deleted #292 cookie-bridge grant test — same access-token/org_id
// assertions, different transport.
public class OidcAuthorizationCodePkceForWasmClientTests
{
    [Fact]
    public async Task AuthorizationCodeWithPkce_ForWasmClient_ReturnsAccessTokenWithOrgId()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            await using var factory = new WebApiFactory(connStr);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            const string email = "wasm-user@test.com";
            const string password = "ValidPass123!";
            var baseUri = new Uri("https://books.koalasoft.se/");
            var redirectUri = new Uri(baseUri, "authentication/login-callback");
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

            var (verifier, challenge) = OidcTestHelpers.GeneratePkcePair();

            var authorizeResponse = await client.GetAsync(
                $"/connect/authorize?client_id={WasmClientSeeder.ClientId}&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}&scope=openid%20profile" +
                $"&code_challenge={challenge}&code_challenge_method=S256");
            Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);

            var code = QueryHelpers.ParseQuery(authorizeResponse.Headers.Location!.Query)["code"].ToString();
            Assert.False(string.IsNullOrEmpty(code));

            var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri.ToString(),
                    ["client_id"] = WasmClientSeeder.ClientId,
                    ["code_verifier"] = verifier,
                }));

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
    public async Task AuthorizationCode_WithoutCodeVerifier_IsRejected()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            await using var factory = new WebApiFactory(connStr);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            const string email = "wasm-user-2@test.com";
            const string password = "ValidPass123!";
            var baseUri = new Uri("https://books.koalasoft.se/");
            var redirectUri = new Uri(baseUri, "authentication/login-callback");

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

            var (_, challenge) = OidcTestHelpers.GeneratePkcePair();

            var authorizeResponse = await client.GetAsync(
                $"/connect/authorize?client_id={WasmClientSeeder.ClientId}&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}&scope=openid%20profile" +
                $"&code_challenge={challenge}&code_challenge_method=S256");
            var code = QueryHelpers.ParseQuery(authorizeResponse.Headers.Location!.Query)["code"].ToString();

            // No code_verifier this time - PKCE requires it.
            var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri.ToString(),
                    ["client_id"] = WasmClientSeeder.ClientId,
                }));

            var body = await tokenResponse.Content.ReadAsStringAsync();
            Assert.False(tokenResponse.IsSuccessStatusCode, body);
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }
}
```

Add the PKCE helper to `OidcTestHelpers` (`tests/KoalaBooks.Tests/OidcTests.cs:21-25`), replacing:

```csharp
internal static class OidcTestHelpers
{
    public static string ExtractAntiforgeryToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
}
```

with:

```csharp
internal static class OidcTestHelpers
{
    public static string ExtractAntiforgeryToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    public static (string Verifier, string Challenge) GeneratePkcePair()
    {
        var verifier = WebEncoders.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(
            System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~OidcAuthorizationCodePkceForWasmClientTests"`
Expected: FAIL — compiles (server still has the code path since we haven't removed it), but the PKCE-less server config still requires the removed cookie grant elsewhere; more importantly this confirms the new test harness runs before the cleanup steps below, isolating any breakage to Step 3-5 changes.

- [ ] **Step 3: Remove the custom grant from `Program.cs`**

In `src/KoalaBooks.Web/Program.cs`, change:

```csharp
        options.AllowPasswordFlow()
               .AllowRefreshTokenFlow()
               .AllowAuthorizationCodeFlow()
               .AllowCustomFlow(WasmCookieBridge.GrantType);
```

to:

```csharp
        options.AllowPasswordFlow()
               .AllowRefreshTokenFlow()
               .AllowAuthorizationCodeFlow();
```

Remove the now-unused import at line 3:

```csharp
using KoalaBooks.Domain.Auth;
```

- [ ] **Step 4: Remove the cookie-grant branch from `Token.cshtml.cs`**

In `src/KoalaBooks.Web/Pages/Connect/Token.cshtml.cs`, remove this block from `HandleAsync`:

```csharp
        // #292: bridges the WASM client's ambient Identity cookie session to a bearer token,
        // without going through AddOidcAuthentication()'s RemoteAuthenticationService (which
        // conflicts with AddAuthenticationStateDeserialization() over the AuthenticationStateProvider
        // DI slot).
        if (request.GrantType == WasmCookieBridge.GrantType)
            return await HandleCookieGrantAsync(request);

```

Remove the entire `HandleCookieGrantAsync` method (the last method in the class, using `WasmCookieBridge.CsrfHeaderName`/`CsrfHeaderValue`).

Remove the now-unused imports at the top of the file:

```csharp
using KoalaBooks.Domain.Auth;
using Microsoft.Extensions.Primitives;
```

- [ ] **Step 5: Delete `WasmCookieBridge.cs`**

```bash
git rm src/KoalaBooks.Domain/Auth/WasmCookieBridge.cs
```

- [ ] **Step 6: Build and run tests**

Run: `dotnet build`
Expected: 0 errors, 0 warnings (confirms no remaining references to `WasmCookieBridge`)

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~OidcAuthorizationCodePkceForWasmClientTests|FullyQualifiedName~WasmClientSeedingTests|FullyQualifiedName~OidcLogoutEndpointTests"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Web/Program.cs src/KoalaBooks.Web/Pages/Connect/Token.cshtml.cs tests/KoalaBooks.Tests/OidcTests.cs
git rm src/KoalaBooks.Domain/Auth/WasmCookieBridge.cs
git commit -m "Remove #292 cookie-bridge grant; WASM client now uses authorization code + PKCE"
```

---

## Task 4: Client — swap `CookieBridgeTokenHandler` for `AddOidcAuthentication`

**Files:**
- Modify: `src/KoalaBooks.Client/Program.cs`
- Delete: `src/KoalaBooks.Client/Services/CookieBridgeTokenHandler.cs`
- Modify: `src/KoalaBooks.Client/_Imports.razor`

**Interfaces:**
- Consumes: `WasmClientSeeder.ClientId` value `"koalabooks-wasm"` (hardcoded string here, since Client can't reference `KoalaBooks.Infrastructure`).
- Produces: the `"KoalaBooks.Api"` named `HttpClient` — unchanged name/usage, so `IFiscalYearService`/`IAccountService`/etc. registrations below it (`Program.cs:39-51`) don't need to change.

- [ ] **Step 1: Replace the auth wiring in `Program.cs`**

In `src/KoalaBooks.Client/Program.cs`, replace lines 1-36 (everything from the `using` block through the `AddScoped(sp => ...)` line) with:

```csharp
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

KoalaBooks.Client.TrimmerPreserve.Preserve();

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

// MainLayout and the WASM-rendered pages use MudBlazor components (MudDrawer, MudSnackbar,
// etc.) that depend on services this registers (IBrowserViewportService, popover/dialog
// services, ...).
builder.Services.AddMudServices();

// Standard authorization-code + PKCE flow against the same-origin OpenIddict authority.
// Tokens live in sessionStorage via oidc-client-js defaults; no refresh-token grant, so the
// session ends when the tab/browser closes (see Global Constraints in the plan).
builder.Services.AddOidcAuthentication(options =>
{
    options.ProviderOptions.Authority = builder.HostEnvironment.BaseAddress;
    options.ProviderOptions.ClientId = "koalabooks-wasm";
    options.ProviderOptions.ResponseType = "code";
    options.ProviderOptions.DefaultScopes.Add("email");
    options.ProviderOptions.DefaultScopes.Add("profile");
});

// Same-origin API, so the only authorized URL is the app's own base address.
builder.Services.AddHttpClient("KoalaBooks.Api", client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler(sp =>
    {
        var handler = sp.GetRequiredService<AuthorizationMessageHandler>();
        handler.ConfigureHandler(authorizedUrls: [builder.HostEnvironment.BaseAddress]);
        return handler;
    });
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("KoalaBooks.Api"));
```

Leave the rest of the file (the `IFiscalYearService`/`IAccountService`/... registrations and `await builder.Build().RunAsync();`) unchanged.

- [ ] **Step 2: Delete `CookieBridgeTokenHandler.cs`**

```bash
git rm src/KoalaBooks.Client/Services/CookieBridgeTokenHandler.cs
```

- [ ] **Step 3: Update `_Imports.razor`**

Replace `src/KoalaBooks.Client/_Imports.razor` in full:

```
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.WebAssembly.Authentication
@using KoalaBooks.Client.Pages
@using KoalaBooks.Components
@using MudBlazor
```

`KoalaBooks.Client.Pages` is needed for `App.razor` (Task 6, Step 3) to resolve the bare `Authentication` type name — `Authentication.razor` (Task 5) has no `@namespace` override, so under this repo's folder-based Razor namespacing (see `KoalaBooks.Components/Pages/Home.razor` → `KoalaBooks.Components.Pages.Home`) it resolves to `KoalaBooks.Client.Pages.Authentication`, not `KoalaBooks.Client.Authentication`. Without this import, `typeof(Authentication)` in `App.razor` fails to compile with CS0246.

- [ ] **Step 4: Build**

Run: `dotnet build src/KoalaBooks.Client`
Expected: errors — `App` type doesn't exist yet (created in Task 6). This is expected; proceed to Task 5 and 6 before the next full build check.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Client/Program.cs src/KoalaBooks.Client/_Imports.razor
git rm src/KoalaBooks.Client/Services/CookieBridgeTokenHandler.cs
git commit -m "Client: replace cookie-bridge token handler with AddOidcAuthentication"
```

---

## Task 5: Client — add the `authentication/{action}` route

**Files:**
- Create: `src/KoalaBooks.Client/Pages/Authentication.razor`

**Interfaces:**
- Consumes: `RemoteAuthenticatorView` from `Microsoft.AspNetCore.Components.WebAssembly.Authentication` (package already referenced in `KoalaBooks.Client.csproj`).
- Produces: the `authentication/login-callback` and `authentication/logout-callback` routes that `WasmClientSeeder`'s registered redirect URIs point at (Task 1).

- [ ] **Step 1: Create the component**

Create `src/KoalaBooks.Client/Pages/Authentication.razor`:

```razor
@page "/authentication/{action}"
@using Microsoft.AspNetCore.Components.WebAssembly.Authentication

<RemoteAuthenticatorView Action="@Action" />

@code {
    [Parameter] public string? Action { get; set; }
}
```

This component is not reachable by the router yet: `Routes.razor` (`src/KoalaBooks.Components/Routes.razor`) only scans the `KoalaBooks.Components` assembly (`AppAssembly="typeof(Routes).Assembly"`) for routable pages, not `KoalaBooks.Client` — Client itself has never hosted a routable page before. Task 6, Step 2 fixes this by giving `Routes.razor` an `AdditionalAssemblies` parameter and having `App.razor` (Task 6, Step 3) pass in `typeof(Authentication).Assembly`. Don't try to navigate to `/authentication/*` until Task 6 is done.

- [ ] **Step 2: Commit**

```bash
git add src/KoalaBooks.Client/Pages/Authentication.razor
git commit -m "Client: add authentication/{action} route for RemoteAuthenticatorView"
```

---

## Task 6: Client — standalone hosting (root components, `index.html`, router fix)

**Files:**
- Create: `src/KoalaBooks.Client/App.razor`
- Create: `src/KoalaBooks.Client/wwwroot/index.html`
- Create: `src/KoalaBooks.Client/wwwroot/js/download.js` (copy from `src/KoalaBooks.Web/wwwroot/js/download.js`)
- Create: `src/KoalaBooks.Client/wwwroot/app.css` (copy from `src/KoalaBooks.Web/wwwroot/app.css`)
- Modify: `src/KoalaBooks.Components/Routes.razor` (add an `AdditionalAssemblies` parameter so the router can also scan `KoalaBooks.Client`'s `Authentication.razor`)

**Interfaces:**
- Produces: `KoalaBooks.Client.App` — the root component mounted at `#app`, replacing what `KoalaBooks.Web.Components.App` (deleted in Task 7) did for the hosted model.

- [ ] **Step 1: Copy static assets**

```bash
cp src/KoalaBooks.Web/wwwroot/js/download.js src/KoalaBooks.Client/wwwroot/js/download.js
cp src/KoalaBooks.Web/wwwroot/app.css src/KoalaBooks.Client/wwwroot/app.css
```

- [ ] **Step 2: Make `Routes.razor` accept `AdditionalAssemblies` as a parameter**

`Routes.razor`'s `Router` currently hardcodes `AppAssembly="typeof(Routes).Assembly"` (`KoalaBooks.Components`, where all 20 page components live). `Authentication.razor` (Task 5) lives in `KoalaBooks.Client` instead, which the router needs to scan too — but `KoalaBooks.Components` cannot reference `KoalaBooks.Client` by type (that would be a circular project reference, since `Client` already references `Components`). So `Routes.razor` takes the extra assemblies as a parameter from its caller, which — being in `KoalaBooks.Client` — can freely name `KoalaBooks.Client` types.

Replace `src/KoalaBooks.Components/Routes.razor` in full:

```razor
<CascadingAuthenticationState>
    <Router AppAssembly="typeof(Routes).Assembly"
            AdditionalAssemblies="AdditionalAssemblies"
            NotFoundPage="typeof(KoalaBooks.Components.Pages.NotFound)">
        <Found Context="routeData">
            <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(KoalaBooks.Components.Layout.MainLayout)">
                <NotAuthorized>
                    <RedirectToLogin />
                </NotAuthorized>
            </AuthorizeRouteView>
            <FocusOnNavigate RouteData="routeData" Selector="h1" />
        </Found>
    </Router>
</CascadingAuthenticationState>

@code {
    [Parameter] public IEnumerable<System.Reflection.Assembly>? AdditionalAssemblies { get; set; }
}
```

This mirrors exactly what the deleted `KoalaBooks.Web/Components/App.razor` did via `.AddAdditionalAssemblies(typeof(KoalaBooks.Client._Imports).Assembly)` at the hosting layer (`Program.cs:358-360`) — moving that responsibility from the (now-deleted) server-side hosting call into a parameter the shared `Routes.razor` component exposes, since there's no longer a server-side host to supply it.

- [ ] **Step 3: Create `App.razor`**

Create `src/KoalaBooks.Client/App.razor`:

```razor
<MudThemeProvider Theme="_theme" />
<MudPopoverProvider />
<MudDialogProvider />
<MudSnackbarProvider />
<Routes AdditionalAssemblies="new[] { typeof(Authentication).Assembly }" />

@code {
    private readonly MudTheme _theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#2D6A4F",
            Secondary = "#1E293B",
            Surface = "#FFFFFF",
            Background = "#F8FAFC",
            AppbarBackground = "#1E293B",
            DrawerBackground = "#1E293B",
            DrawerText = "#CBD5E1",
            DrawerIcon = "#94A3B8",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Inter", "sans-serif"],
            },
        },
    };
}
```

`Routes.razor` already wraps its `<Router>` in `<CascadingAuthenticationState>`, so `App.razor` does not add a second one. `Authentication` resolves via the `KoalaBooks.Client.Pages` namespace, imported by the `@using KoalaBooks.Client.Pages` line added to `_Imports.razor` in Task 4, Step 3 — being in the same project is not sufficient on its own, since `App.razor` (namespace `KoalaBooks.Client`) and `Authentication.razor` (namespace `KoalaBooks.Client.Pages`) are in different namespaces. `Authentication.razor`'s own `@page` route makes it part of `typeof(Authentication).Assembly`, i.e. `KoalaBooks.Client`'s own assembly.

- [ ] **Step 4: Create `wwwroot/index.html`**

Create `src/KoalaBooks.Client/wwwroot/index.html`:

```html
<!DOCTYPE html>
<html lang="sv">

<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>KoalaBooks</title>
    <base href="/" />
    <link href="https://fonts.googleapis.com/css?family=Roboto:300,400,500,700&display=swap" rel="stylesheet" />
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&display=swap" rel="stylesheet" />
    <link href="https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;500&display=swap" rel="stylesheet" />
    <link href="_content/MudBlazor/MudBlazor.min.css" rel="stylesheet" />
    <link rel="stylesheet" href="app.css" />
    <link rel="stylesheet" href="KoalaBooks.Client.styles.css" />
</head>

<body>
    <div id="app">
        <div style="padding:2rem; font-family:sans-serif;">Laddar KoalaBooks…</div>
    </div>

    <div id="blazor-error-ui" data-nosnippet>
        Ett ohanterat fel har inträffat.
        <a href="." class="reload">Ladda om</a>
        <span class="dismiss">🗙</span>
    </div>

    <script src="_framework/blazor.webassembly.js"></script>
    <script src="js/download.js"></script>
    <script src="_content/MudBlazor/MudBlazor.min.js"></script>
</body>

</html>
```

`KoalaBooks.Client.styles.css` is the standard Blazor-generated scoped-CSS bundle name for the top-level app project (analogous to `KoalaBooks.Web.styles.css` in the deleted `App.razor`) — it's produced automatically at publish/build time from every referenced project's `.razor.css` files (`MainLayout.razor.css`, `ReconnectModal.razor.css`, `AccountSearchDropdown.razor.css`), no manual step needed.

- [ ] **Step 5: Build the Client project standalone**

Run: `dotnet build src/KoalaBooks.Client`
Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Client/App.razor src/KoalaBooks.Client/wwwroot src/KoalaBooks.Components/Routes.razor
git commit -m "Client: add standalone root components, index.html, and router wiring for Authentication.razor"
```

---

## Task 7: Web — remove Blazor Components hosting

**Files:**
- Modify: `src/KoalaBooks.Web/Program.cs`
- Delete: `src/KoalaBooks.Web/Components/App.razor`
- Delete: `src/KoalaBooks.Web/Components/_Imports.razor`
- Modify: `src/KoalaBooks.Web/KoalaBooks.Web.csproj`

**Interfaces:**
- No new interfaces — this is pure removal. `Controllers/Api/*`, `AddIdentity`, `AddOpenIddict`, `AddRazorPages`, Hangfire registration/dashboard are all untouched.

- [ ] **Step 1: Trim `Program.cs` imports**

Remove these now-unused `using` directives (top of `src/KoalaBooks.Web/Program.cs`):

```csharp
using KoalaBooks.Web.Components;
using MudBlazor;
using MudBlazor.Services;
```

- [ ] **Step 2: Remove `AddMudServices`**

Delete this block (currently `Program.cs:207-214`):

```csharp
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.MaxDisplayedSnackbars = 3;
    config.SnackbarConfiguration.VisibleStateDuration = 3000;
    config.SnackbarConfiguration.HideTransitionDuration = 300;
    config.SnackbarConfiguration.ShowTransitionDuration = 300;
});

```

- [ ] **Step 3: Remove Components/WASM hosting registration**

Change (currently `Program.cs:219-223`):

```csharp
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization(options => options.SerializeAllClaims = true);
```

to:

```csharp
builder.Services.AddRazorPages();
```

- [ ] **Step 4: Remove the `MapRazorComponents` chain**

Change (currently `Program.cs:353-360`):

```csharp
app.MapStaticAssets();
app.MapRazorPages();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(
        typeof(KoalaBooks.Components.Pages.Home).Assembly,
        typeof(KoalaBooks.Client._Imports).Assembly);
```

to:

```csharp
app.MapRazorPages();
```

`MapStaticAssets()` is dropped too — it mapped `KoalaBooks.Web`'s own `wwwroot` (`app.css`, `js/download.js`), which moved to `KoalaBooks.Client/wwwroot` in Task 6. `Login.cshtml`/`Register.cshtml` use inline `<style>` blocks (verified — no external stylesheet dependency), so Web has no remaining static-asset consumers.

- [ ] **Step 5: Delete the Web-hosted `App.razor` and its `_Imports.razor`**

```bash
git rm src/KoalaBooks.Web/Components/App.razor
git rm src/KoalaBooks.Web/Components/_Imports.razor
```

If `src/KoalaBooks.Web/Components/` is now empty, remove the directory too.

- [ ] **Step 6: Drop unneeded project/package references from `KoalaBooks.Web.csproj`**

In `src/KoalaBooks.Web/KoalaBooks.Web.csproj`, remove these two `ProjectReference` lines:

```xml
    <ProjectReference Include="..\KoalaBooks.Client\KoalaBooks.Client.csproj" />
    <ProjectReference Include="..\KoalaBooks.Components\KoalaBooks.Components.csproj" />
```

and remove these two `PackageReference` lines:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.Server" Version="10.0.10" />
    <PackageReference Include="MudBlazor" Version="9.7.0" />
```

- [ ] **Step 7: Build**

Run: `dotnet build`
Expected: 0 errors, 0 warnings. If MudBlazor/Components-namespace errors appear anywhere else in `KoalaBooks.Web`, grep for `MudBlazor` or `KoalaBooks.Components` under `src/KoalaBooks.Web` to find remaining references (there should be none outside what was just removed) and remove them too.

- [ ] **Step 8: Run the full integration test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS — the design spec calls this out explicitly: it exercises `KoalaBooks.Web`'s API surface directly via `WebApiFactory` and shouldn't care about Components hosting being removed, but must be re-run to confirm no hidden coupling.

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Web/Program.cs src/KoalaBooks.Web/KoalaBooks.Web.csproj
git rm -r src/KoalaBooks.Web/Components
git commit -m "Web: remove Blazor Components/WASM hosting, keep pure API + OpenIddict + Razor Pages"
```

---

## Task 8: Client — logout button uses `NavigateToLogout`

**Files:**
- Modify: `src/KoalaBooks.Components/Layout/MainLayout.razor:17-26`
- Modify: `src/KoalaBooks.Components/KoalaBooks.Components.csproj`
- Modify: `src/KoalaBooks.Components/_Imports.razor`

**Why:** the current logout button does a plain HTML form `POST /account/logout`, which only clears the Identity cookie. That leaves the SPA's own OIDC session (sessionStorage tokens, in-memory `RemoteAuthenticationService` state) untouched — the user would appear logged out on the next page load's redirect, but any already-cached access token keeps working until it expires. `NavigateToLogout` runs the library's real sign-out (clears local state, then redirects through `/connect/logout`, Task 2), which is required for the design spec's own verification step ("logout → session cleared → protected route redirects to login again").

**Interfaces:**
- Consumes: `Microsoft.AspNetCore.Components.WebAssembly.Authentication.NavigationManagerExtensions.NavigateToLogout(this NavigationManager, string logoutPath, string? returnUrl = null)`.

- [ ] **Step 1: Add the package reference**

In `src/KoalaBooks.Components/KoalaBooks.Components.csproj`, add to the existing `<ItemGroup>` with `PackageReference`:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.Authentication" Version="10.0.10" />
```

- [ ] **Step 2: Add the `@using` to `_Imports.razor`**

Append to `src/KoalaBooks.Components/_Imports.razor`:

```
@using Microsoft.AspNetCore.Components.WebAssembly.Authentication
```

- [ ] **Step 3: Change the logout button**

In `src/KoalaBooks.Components/Layout/MainLayout.razor`, replace lines 17-26:

```razor
        <AuthorizeView>
            <Authorized>
                <MudText Typo="Typo.body2" Class="mr-2" Style="color:#CBD5E1;">@context.User.Identity?.Name</MudText>
                <form method="post" action="/account/logout" style="display:inline;">
                    <AntiforgeryToken />
                    <MudIconButton Icon="@Icons.Material.Outlined.Logout" Color="Color.Inherit" Size="Size.Small"
                                   title="Logga ut" ButtonType="ButtonType.Submit" />
                </form>
            </Authorized>
        </AuthorizeView>
```

with:

```razor
        <AuthorizeView>
            <Authorized>
                <MudText Typo="Typo.body2" Class="mr-2" Style="color:#CBD5E1;">@context.User.Identity?.Name</MudText>
                <MudIconButton Icon="@Icons.Material.Outlined.Logout" Color="Color.Inherit" Size="Size.Small"
                               title="Logga ut" OnClick="Logout" />
            </Authorized>
        </AuthorizeView>
```

Add to the `@code` block (`MainLayout.razor:104` onward), alongside the existing fields:

```csharp
    private void Logout() => Navigation.NavigateToLogout("authentication/logout");
```

- [ ] **Step 4: Build**

Run: `dotnet build src/KoalaBooks.Client`
Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Components/Layout/MainLayout.razor src/KoalaBooks.Components/KoalaBooks.Components.csproj src/KoalaBooks.Components/_Imports.razor
git commit -m "MainLayout: logout via NavigateToLogout instead of raw form POST to /account/logout"
```

---

## Task 9: Deployment — Client static-file container + prod Caddyfile split

**Files:**
- Create: `src/KoalaBooks.Client/Dockerfile`
- Create: `src/KoalaBooks.Client/Caddyfile` (the static-file server's own config, distinct from the root reverse-proxy `Caddyfile`)
- Modify: `docker-compose.yml`
- Modify: `Caddyfile` (repo root)

**Interfaces:**
- Produces: a `client` Docker Compose service, image `koalabooks-client:latest`, serving the published `KoalaBooks.Client` static files with SPA fallback on port 80 internally.

- [ ] **Step 1: Write the Client Dockerfile**

Create `src/KoalaBooks.Client/Dockerfile`:

```dockerfile
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/KoalaBooks.Client/KoalaBooks.Client.csproj src/KoalaBooks.Client/
COPY src/KoalaBooks.Domain/KoalaBooks.Domain.csproj src/KoalaBooks.Domain/
COPY src/KoalaBooks.Components/KoalaBooks.Components.csproj src/KoalaBooks.Components/
RUN dotnet restore src/KoalaBooks.Client -a $TARGETARCH

COPY . .
RUN dotnet publish src/KoalaBooks.Client -c Release -o /app --no-restore -a $TARGETARCH

FROM caddy:2-alpine
COPY --from=build /app/wwwroot /srv
COPY src/KoalaBooks.Client/Caddyfile /etc/caddy/Caddyfile
EXPOSE 80
```

- [ ] **Step 2: Write the static-file Caddyfile**

Create `src/KoalaBooks.Client/Caddyfile`:

```
:80 {
    root * /srv
    file_server
    try_files {path} /index.html
}
```

- [ ] **Step 3: Add the `client` service to `docker-compose.yml`**

In `docker-compose.yml`, add a new service (after `web`, before `postgres`):

```yaml
  client:
    image: ghcr.io/${GITHUB_REPOSITORY_OWNER:-local}/koalabooks-client:latest
    build:
      context: .
      dockerfile: src/KoalaBooks.Client/Dockerfile
    restart: unless-stopped
```

Add a `PublicOrigin` environment variable to the existing `web` service's `environment:` list (it's read by `WasmClientSeeder` via `Program.cs`, Task 1):

```yaml
      - PublicOrigin=https://books.koalasoft.se
```

- [ ] **Step 4: Update the root Caddyfile to split routing**

Replace the `books.koalasoft.se` block in `Caddyfile`:

```
books.koalasoft.se {
    reverse_proxy web:8080
}
```

with:

```
books.koalasoft.se {
    @backend path /api/* /connect/* /hangfire /hangfire/*
    reverse_proxy @backend web:8080
    reverse_proxy client:80
}
```

- [ ] **Step 5: Verify locally with Docker Compose**

Run: `docker compose build client web`
Expected: both images build with 0 errors.

Run: `docker compose up -d client web postgres caddy`
Then: `curl -I http://localhost` (or the configured local port) should return the SPA's `index.html` (`200`, `content-type: text/html`), and `curl -I http://localhost/connect/authorize` should reach `web` (any non-`404` response, since Caddy only needs to prove the path match routes there, not that the request itself is well-formed).

Tear down: `docker compose down`

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Client/Dockerfile src/KoalaBooks.Client/Caddyfile docker-compose.yml Caddyfile
git commit -m "Add koalabooks-client static-file container; split Caddy routing by path"
```

---

## Task 10: Deployment — PR-preview compose + CI workflow updates

**Files:**
- Modify: `docker-compose.pr-preview.yml`
- Modify: `.github/workflows/pr-preview.yml`
- Modify: `.github/workflows/pr-preview-cleanup.yml`

**Interfaces:**
- Consumes: the existing `__PR_NUMBER__`/`__OWNER__` sed-template placeholders already used elsewhere in `docker-compose.pr-preview.yml` and `pr-preview.yml` (no new templating mechanism introduced).

- [ ] **Step 1: Add the `client` service to the PR-preview compose template**

In `docker-compose.pr-preview.yml`, add a new service after `web`:

```yaml
  client:
    image: ghcr.io/__OWNER__/koalabooks-client:pr-__PR_NUMBER__
    networks:
      - internal
      - pr-previews
```

Add `PublicOrigin` to the existing `web` service's `environment:` list, reusing the same `__PR_NUMBER__` placeholder the workflow's `sed` step already substitutes:

```yaml
      - PublicOrigin=https://pr-__PR_NUMBER__.books.koalasoft.se
```

- [ ] **Step 2: Build and push the client image in CI**

In `.github/workflows/pr-preview.yml`, add a build-and-push step for the client image right after the existing "Build and push PR image" step (which builds `koalabooks-web`):

```yaml
      - name: Build and push PR client image
        uses: docker/build-push-action@v7
        with:
          context: .
          file: src/KoalaBooks.Client/Dockerfile
          platforms: linux/amd64,linux/arm64
          push: true
          tags: ${{ env.REGISTRY }}/${{ github.repository_owner }}/koalabooks-client:pr-${{ github.event.pull_request.number }}
          cache-from: type=registry,ref=${{ env.REGISTRY }}/${{ github.repository_owner }}/koalabooks-client:buildcache
```

- [ ] **Step 3: Update the generated per-PR Caddy snippet**

In the same workflow's "Deploy PR environment" step, replace this line:

```bash
            printf 'pr-%s.books.koalasoft.se {\n    reverse_proxy pr-%s-web-1:8080\n}\n' \
              "${PR_NUMBER}" "${PR_NUMBER}" \
              > /opt/koalabooks/caddy-snippets/pr-${PR_NUMBER}.caddy
```

with:

```bash
            printf 'pr-%s.books.koalasoft.se {\n    @backend path /api/* /connect/* /hangfire /hangfire/*\n    reverse_proxy @backend pr-%s-web-1:8080\n    reverse_proxy pr-%s-client-1:80\n}\n' \
              "${PR_NUMBER}" "${PR_NUMBER}" "${PR_NUMBER}" \
              > /opt/koalabooks/caddy-snippets/pr-${PR_NUMBER}.caddy
```

- [ ] **Step 4: Clean up the client image on PR close**

In the same workflow's `cleanup` job, after the existing:

```yaml
            docker rmi ghcr.io/${OWNER}/koalabooks-web:pr-${PR_NUMBER} || true
```

add:

```yaml
            docker rmi ghcr.io/${OWNER}/koalabooks-client:pr-${PR_NUMBER} || true
```

Also add a "Delete GHCR package version" step for `koalabooks-client`, mirroring the existing one for `koalabooks-web` (same `github-script` body, `package_name: 'koalabooks-client'`).

- [ ] **Step 5: Update the weekly stale-image cleanup workflow**

In `.github/workflows/pr-preview-cleanup.yml`, the "Delete stale GHCR images" step currently only checks `package_name: 'koalabooks-web'`. Duplicate that step (or wrap the body in a loop over `['koalabooks-web', 'koalabooks-client']`) so `koalabooks-client` images for closed PRs get pruned too.

- [ ] **Step 6: Commit**

```bash
git add docker-compose.pr-preview.yml .github/workflows/pr-preview.yml .github/workflows/pr-preview-cleanup.yml
git commit -m "PR previews: build/deploy koalabooks-client alongside koalabooks-web, split Caddy routing"
```

Note: this workflow change can't be fully verified until a real PR triggers it — the next step in this plan's execution should be to open the PR for this whole branch so the updated workflow runs for real, per the design spec's own verification plan ("Local/dev pass... confirm static files and proxied API paths both resolve correctly").

---

## Task 11: Full verification pass

**Files:** none (verification only).

**Note on local dev / Aspire:** the design spec's testing plan asks to check whether `KoalaBooks.AppHost` is used for local dev before deciding how to verify the same-origin split locally. It is (`src/KoalaBooks.AppHost/AppHost.cs`), but today it only orchestrates Postgres + the `KoalaBooks.Web` project — it has no resource for `KoalaBooks.Client` or Caddy, so day-to-day `dotnet run --project src/KoalaBooks.AppHost` inner-loop dev doesn't currently exercise the split-origin topology at all. Wiring Aspire to reverse-proxy Client+Web the same way Caddy does in prod is a bigger local-dev-experience investment than this sub-project's scope justifies (its own explicitly-out-of-scope list is about page conversion, not tooling). This plan instead verifies the real topology via Docker Compose (Step 3 below), which is what prod and PR previews actually run. If day-to-day Aspire-based dev turns out to be painful without the split, file it as a fast-follow rather than folding it into this plan.

- [ ] **Step 1: Full solution build**

Run: `dotnet build`
Expected: 0 warnings, 0 errors across all projects.

- [ ] **Step 2: Full integration test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS, including every test touched in Tasks 1-3.

Run: `dotnet test tests/KoalaBooks.ComponentTests`
Expected: PASS. This project project-references `KoalaBooks.Components` directly (bUnit specs for `AccountsPageTests`, `JournalPageTests`, and 17 others), which Tasks 6 and 8 modify (`Routes.razor`'s new `AdditionalAssemblies` parameter, `MainLayout.razor`'s logout button, the new `Microsoft.AspNetCore.Components.WebAssembly.Authentication` package reference) — CI's solution-wide `dotnet test` would eventually catch a regression here, but it should be verified locally before opening the PR, not left to CI.

- [ ] **Step 3: Local Docker Compose pass**

Run: `docker compose up -d`
Then verify:
- `curl -I https://localhost` (via whatever local hostname/port your Caddy setup uses) returns the SPA shell.
- The `web` container logs show `WasmClientSeeder` seeding successfully (no exceptions) on startup.

Tear down: `docker compose down`

- [ ] **Step 4: Manual browser pass of the full OIDC flow**

1. Open the app while logged out. Confirm it redirects through `/connect/authorize` → `Login.cshtml` (the existing Identity login form).
2. Log in. Confirm the redirect lands back on `authentication/login-callback`, exchanges the code, and the SPA renders with real data (e.g. `MainLayout`'s nav badges load).
3. Navigate to `/review` (the pre-existing WASM proof point). Confirm it still works — this is the one regression the design spec explicitly calls out as unacceptable.
4. Click logout. Confirm the SPA clears its session and redirects through `authentication/logout-callback` → `/connect/logout` → back to a logged-out state.
5. Try navigating directly to a protected route (e.g. `/journal`) post-logout. Confirm it redirects to login again, not a silently-restored session (this is exactly what Task 2's `/connect/logout` fix targets).

- [ ] **Step 5: Open the PR**

Once all of the above pass, follow `superpowers:finishing-a-development-branch` to open the PR — this is what actually exercises the CI workflow changes from Task 10 against the real PR-preview infrastructure.
