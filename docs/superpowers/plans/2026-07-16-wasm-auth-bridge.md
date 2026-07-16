# WASM/Auto Auth Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the auth plumbing (issue #257) that lets a future browser-hosted WASM/Auto Blazor component see the same login state as server components and make API calls OpenIddict will accept — without adding a real WASM feature page yet.

**Architecture:** Two independent tracks. Track A: the built-in ASP.NET Core `AddAuthenticationStateSerialization`/`AddAuthenticationStateDeserialization` pair serializes the server's claims into the page at prerender and deserializes them client-side for `AuthorizeView`/UI state. Track B: a new public, PKCE-required OpenIddict client plus Microsoft's own generic OIDC client library (`AddOidcAuthentication` + `RemoteAuthenticatorView`) drives a silent (hidden-iframe) authorization-code exchange against the app's own already-configured `/connect/authorize` + `/connect/token` endpoints, giving WASM a real bearer token. Along the way, a real gap is fixed: the authorization-code flow (`Authorize.cshtml.cs`) doesn't currently set the `org_id` claim the password grant does, which would silently break tenant scoping for any token minted this way.

**Tech Stack:** ASP.NET Core 10 / Blazor Web App, OpenIddict 7.5.0, `Microsoft.AspNetCore.Components.WebAssembly.Server` (Track A, server), `Microsoft.AspNetCore.Components.WebAssembly.Authentication` (Track A + B, client), xunit + `WebApplicationFactory` + Testcontainers Postgres (existing test stack).

## Global Constraints

- No real `InteractiveWebAssembly`/`InteractiveAuto` page is added — see `docs/superpowers/specs/2026-07-16-wasm-auth-bridge-design.md` non-goals. The new `KoalaBooks.Client` project and its `/authentication/{action}` route are infrastructure only; nothing in the Web project references them yet, so no existing behavior changes for real users.
- No new OpenIddict grant type. Everything rides the already-configured `/connect/authorize` + `/connect/token` endpoints (`options.AllowAuthorizationCodeFlow()` etc. in `src/KoalaBooks.Web/Program.cs`).
- All new NuGet packages pinned to `10.0.9`, matching the version already used for `Microsoft.AspNetCore.OpenApi`/`Microsoft.EntityFrameworkCore.Design` in `src/KoalaBooks.Web/KoalaBooks.Web.csproj`.
- The new `KoalaBooks.Client` project targets `net10.0` and references no other KoalaBooks project — discovered during Task 5 that `KoalaBooks.Application` itself has a (pre-existing, backwards) `ProjectReference` to `KoalaBooks.Infrastructure`, which carries a `FrameworkReference` to `Microsoft.AspNetCore.App` incompatible with browser-wasm. Nothing in Tasks 5-6 uses any Application type, so the reference is dropped rather than worked around. A future real WASM page consuming Application-layer services will need #79/#224-style interface extraction resolved first regardless.
- Building `KoalaBooks.Client` for the first time will make NuGet fetch the `browser-wasm` runtime pack (not yet cached locally) — this needs network access and may take a few minutes the first time.

---

### Task 1: Fix the `org_id` gap with a shared OpenIddict identity-building helper

**Files:**
- Create: `src/KoalaBooks.Web/Pages/Connect/OpenIddictIdentityBuilder.cs`
- Modify: `src/KoalaBooks.Web/Pages/Connect/Token.cshtml.cs:69-92`
- Modify: `src/KoalaBooks.Web/Pages/Connect/Authorize.cshtml.cs:48-67`
- Test: `tests/KoalaBooks.Tests/OpenIddictIdentityBuilderTests.cs`

**Interfaces:**
- Produces: `KoalaBooks.Web.Pages.Connect.OpenIddictIdentityBuilder.BuildPrincipal(ApplicationUser user, string userId, IEnumerable<string> scopes) -> ClaimsPrincipal` — used by Task 1's own two call sites; no later task depends on it directly.

- [ ] **Step 1: Write the failing unit test**

Create `tests/KoalaBooks.Tests/OpenIddictIdentityBuilderTests.cs`:

```csharp
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Web.Pages.Connect;
using OpenIddict.Abstractions;

namespace KoalaBooks.Tests;

public class OpenIddictIdentityBuilderTests
{
    [Fact]
    public void BuildPrincipal_UserWithOrganisation_SetsOrgIdOnAccessToken()
    {
        var user = new ApplicationUser
        {
            UserName = "test@koalabooks.test",
            Email = "test@koalabooks.test",
            DisplayName = "Test User",
            OrganisationId = 42
        };

        var principal = OpenIddictIdentityBuilder.BuildPrincipal(user, "user-id-1", ["profile", "email"]);

        var orgClaim = principal.FindFirst("org_id");
        Assert.NotNull(orgClaim);
        Assert.Equal("42", orgClaim.Value);
        Assert.Contains(OpenIddictConstants.Destinations.AccessToken, orgClaim.GetDestinations());
    }

    [Fact]
    public void BuildPrincipal_UserWithoutOrganisation_DoesNotSetOrgId()
    {
        var user = new ApplicationUser
        {
            UserName = "test@koalabooks.test",
            Email = "test@koalabooks.test",
            DisplayName = "Test User",
            OrganisationId = null
        };

        var principal = OpenIddictIdentityBuilder.BuildPrincipal(user, "user-id-1", ["profile"]);

        Assert.Null(principal.FindFirst("org_id"));
    }

    [Fact]
    public void BuildPrincipal_SetsSubjectEmailAndName()
    {
        var user = new ApplicationUser
        {
            UserName = "someone@koalabooks.test",
            Email = "someone@koalabooks.test",
            DisplayName = "Someone Person",
            OrganisationId = 7
        };

        var principal = OpenIddictIdentityBuilder.BuildPrincipal(user, "user-id-2", ["profile", "email"]);

        Assert.Equal("user-id-2", principal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value);
        Assert.Equal("someone@koalabooks.test", principal.FindFirst(OpenIddictConstants.Claims.Email)?.Value);
        Assert.Equal("Someone Person", principal.FindFirst(OpenIddictConstants.Claims.Name)?.Value);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails to compile**

Run: `dotnet test tests/KoalaBooks.Tests --filter OpenIddictIdentityBuilderTests`
Expected: build error — `OpenIddictIdentityBuilder` does not exist.

- [ ] **Step 3: Create the shared helper**

Create `src/KoalaBooks.Web/Pages/Connect/OpenIddictIdentityBuilder.cs`:

```csharp
using KoalaBooks.Infrastructure.Data;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using System.Security.Claims;

namespace KoalaBooks.Web.Pages.Connect;

// Shared by Token.cshtml.cs (password grant) and Authorize.cshtml.cs (authorization code grant)
// so both OpenIddict flows mint identically-shaped principals, including org_id for tenant scoping.
public static class OpenIddictIdentityBuilder
{
    public static ClaimsPrincipal BuildPrincipal(ApplicationUser user, string userId, IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity.SetClaim(OpenIddictConstants.Claims.Subject, userId)
                .SetClaim(OpenIddictConstants.Claims.Email, user.Email ?? string.Empty)
                .SetClaim(OpenIddictConstants.Claims.Name, user.DisplayName ?? user.Email ?? user.UserName ?? string.Empty);

        if (user.OrganisationId.HasValue)
            identity.SetClaim("org_id", user.OrganisationId.Value.ToString());

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        // All claims (including org_id) go to the access token via the catch-all branch.
        // Email and Name also go to the identity token for OIDC clients.
        principal.SetDestinations(claim => claim.Type switch
        {
            OpenIddictConstants.Claims.Email or OpenIddictConstants.Claims.Name =>
                [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
            _ => [OpenIddictConstants.Destinations.AccessToken]
        });

        return principal;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/KoalaBooks.Tests --filter OpenIddictIdentityBuilderTests`
Expected: 3 passed.

- [ ] **Step 5: Refactor `Token.cshtml.cs` to use the helper**

In `src/KoalaBooks.Web/Pages/Connect/Token.cshtml.cs`, replace lines 69-90:

```csharp
        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity.SetClaim(OpenIddictConstants.Claims.Subject, await _userManager.GetUserIdAsync(user))
                .SetClaim(OpenIddictConstants.Claims.Email, user.Email ?? string.Empty)
                .SetClaim(OpenIddictConstants.Claims.Name, user.DisplayName ?? user.Email ?? user.UserName ?? string.Empty);

        if (user.OrganisationId.HasValue)
            identity.SetClaim("org_id", user.OrganisationId.Value.ToString());

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        // All claims (including org_id) go to the access token via the catch-all branch.
        // Email and Name also go to the identity token for OIDC clients.
        principal.SetDestinations(claim => claim.Type switch
        {
            OpenIddictConstants.Claims.Email or OpenIddictConstants.Claims.Name =>
                [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
            _ => [OpenIddictConstants.Destinations.AccessToken]
        });
```

with:

```csharp
        var principal = OpenIddictIdentityBuilder.BuildPrincipal(
            user, await _userManager.GetUserIdAsync(user), request.GetScopes());
```

(The `return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);` line right after stays unchanged.)

- [ ] **Step 6: Fix and refactor `Authorize.cshtml.cs` to use the helper**

In `src/KoalaBooks.Web/Pages/Connect/Authorize.cshtml.cs`, replace lines 48-67:

```csharp
        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity.SetClaim(OpenIddictConstants.Claims.Subject, await _userManager.GetUserIdAsync(user))
                .SetClaim(OpenIddictConstants.Claims.Email, user.Email ?? string.Empty)
                .SetClaim(OpenIddictConstants.Claims.Name, user.DisplayName ?? user.Email ?? user.UserName ?? string.Empty);

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetDestinations(claim => claim.Type switch
        {
            OpenIddictConstants.Claims.Email or
            OpenIddictConstants.Claims.Name =>
                [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
            _ => [OpenIddictConstants.Destinations.AccessToken]
        });

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
```

with:

```csharp
        var principal = OpenIddictIdentityBuilder.BuildPrincipal(
            user, await _userManager.GetUserIdAsync(user), request.GetScopes());

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
```

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: all tests pass, including the existing `OidcAuthorizationCodeGrantTests.TokenEndpoint_RedeemsAuthorizationCode_ReturnsAccessToken` and `ApiTests.ConnectToken_ValidCredentials_ReturnsAccessTokenWithOrgId` (password grant still carries `org_id` — proves the refactor didn't regress it).

- [ ] **Step 8: Commit**

```bash
git add src/KoalaBooks.Web/Pages/Connect/OpenIddictIdentityBuilder.cs \
        src/KoalaBooks.Web/Pages/Connect/Token.cshtml.cs \
        src/KoalaBooks.Web/Pages/Connect/Authorize.cshtml.cs \
        tests/KoalaBooks.Tests/OpenIddictIdentityBuilderTests.cs
git commit -m "Fix missing org_id claim in authorization-code flow via shared identity builder"
```

---

### Task 2: Register a public, PKCE-required OpenIddict client for the WASM bridge

**Files:**
- Create: `src/KoalaBooks.Infrastructure/Services/WasmClientSeeder.cs`
- Modify: `src/KoalaBooks.Web/Program.cs:279` (insert seeding call after the existing `AspireDashboardSeeder.SeedAsync` call)
- Test: `tests/KoalaBooks.Tests/OidcTests.cs` (append new test class after `OidcClientSeedingTests`, which currently ends at line 248)

**Interfaces:**
- Produces: `KoalaBooks.Infrastructure.Services.WasmClientSeeder.ClientId` (`const string`, value `"koalabooks-wasm"`) and `WasmClientSeeder.SeedAsync(IServiceProvider services, Uri redirectUri) -> Task` — Task 3's integration test uses `ClientId` in its authorize/token requests; Task 6's client-side config uses the same literal `"koalabooks-wasm"` string (different project, can't share the constant — matches existing `"aspire-dashboard"` literal-string convention already used across `AspireDashboardSeeder`/`Program.cs`/tests).

- [ ] **Step 1: Write the failing seeder test**

In `tests/KoalaBooks.Tests/OidcTests.cs`, append this class after the closing brace of `OidcClientSeedingTests` (end of file):

```csharp
public class WasmClientSeedingTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbName;
    private static readonly Uri RedirectUri = new("https://localhost:7154/authentication/login-callback");

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
    public async Task SeedAsync_CreatesPublicClientRequiringPkce()
    {
        using var scope = _sp.CreateScope();
        await WasmClientSeeder.SeedAsync(scope.ServiceProvider, RedirectUri);

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = await manager.FindByClientIdAsync(WasmClientSeeder.ClientId);
        Assert.NotNull(app);

        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, app);

        Assert.Equal(OpenIddictConstants.ClientTypes.Public, descriptor.ClientType);
        Assert.Contains(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange, descriptor.Requirements);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.RefreshToken, descriptor.Permissions);
        Assert.Contains(RedirectUri, descriptor.RedirectUris);
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent()
    {
        using (var scope = _sp.CreateScope())
            await WasmClientSeeder.SeedAsync(scope.ServiceProvider, RedirectUri);

        using (var scope = _sp.CreateScope())
            await WasmClientSeeder.SeedAsync(scope.ServiceProvider, RedirectUri);

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

- [ ] **Step 2: Run the test to verify it fails to compile**

Run: `dotnet test tests/KoalaBooks.Tests --filter WasmClientSeedingTests`
Expected: build error — `WasmClientSeeder` does not exist.

- [ ] **Step 3: Implement the seeder**

Create `src/KoalaBooks.Infrastructure/Services/WasmClientSeeder.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace KoalaBooks.Infrastructure.Services;

public static class WasmClientSeeder
{
    public const string ClientId = "koalabooks-wasm";

    public static async Task SeedAsync(IServiceProvider services, Uri redirectUri)
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
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                OpenIddictConstants.Permissions.Scopes.Email,
                OpenIddictConstants.Permissions.Scopes.Profile,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
            },
            Requirements =
            {
                OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange
            }
        };

        descriptor.RedirectUris.Add(redirectUri);

        var existing = await manager.FindByClientIdAsync(ClientId);
        if (existing is null)
        {
            await manager.CreateAsync(descriptor);
            logger.LogInformation("Created OpenIddict client '{ClientId}' with redirect URI {RedirectUri}", ClientId, redirectUri);
        }
        else
        {
            await manager.UpdateAsync(existing, descriptor);
            logger.LogInformation("Updated OpenIddict client '{ClientId}' with redirect URI {RedirectUri}", ClientId, redirectUri);
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/KoalaBooks.Tests --filter WasmClientSeedingTests`
Expected: 2 passed.

- [ ] **Step 5: Wire the seeder into startup**

In `src/KoalaBooks.Web/Program.cs`, after line 279 (`await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, new Uri(dashboardRedirectUri), dashboardClientSecret);`), insert:

```csharp

        var wasmClientRedirectUri = builder.Configuration["WasmClient:RedirectUri"]
            ?? "https://localhost:7154/authentication/login-callback";
        await WasmClientSeeder.SeedAsync(scope.ServiceProvider, new Uri(wasmClientRedirectUri));
```

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: all tests pass (startup seeding is idempotent, same pattern as `AspireDashboardSeeder` already running on every `WebApiFactory`-backed test).

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Services/WasmClientSeeder.cs \
        src/KoalaBooks.Web/Program.cs \
        tests/KoalaBooks.Tests/OidcTests.cs
git commit -m "Register a public, PKCE-required OpenIddict client for the WASM auth bridge"
```

---

### Task 3: Prove the fixed authorization-code + PKCE flow yields an org_id-bearing token

**Files:**
- Modify: `tests/KoalaBooks.Tests/OidcTests.cs` (extract shared `OidcTestHelpers` class; append new test class at end of file, after `WasmClientSeedingTests` from Task 2)

**Interfaces:**
- Consumes: `KoalaBooks.Infrastructure.Services.WasmClientSeeder.ClientId`, `WasmClientSeeder.SeedAsync(IServiceProvider, Uri)` (Task 2); `KoalaBooks.Web.Pages.Connect.OpenIddictIdentityBuilder` indirectly via the fixed `Authorize.cshtml.cs` (Task 1).
- Produces: `KoalaBooks.Tests.OidcTestHelpers.ExtractAntiforgeryToken(string html) -> string`, shared by `OidcAuthorizationCodeGrantTests` (existing) and the new `OidcSilentPkceForOwnClientTests` in this task.

This is the end-to-end proof the design doc's Testing section calls for: it drives the same silent PKCE exchange the WASM handler will perform, using `WebApplicationFactory` directly instead of a real browser (consistent with the design's non-goal of not requiring a live browser POC).

This test needs the same antiforgery-token-extraction helper `OidcAuthorizationCodeGrantTests` already has privately at the bottom of its class (`tests/KoalaBooks.Tests/OidcTests.cs:85-86`). Rather than duplicate it, extract it into a shared helper both classes use.

- [ ] **Step 1: Extract the shared antiforgery helper**

In `tests/KoalaBooks.Tests/OidcTests.cs`, replace the private method at the end of `OidcAuthorizationCodeGrantTests` (lines 85-86):

```csharp
    private static string ExtractAntiforgeryToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
}
```

with just the closing brace (removing the method):

```csharp
}
```

Then update its one call site inside `OidcAuthorizationCodeGrantTests.TokenEndpoint_RedeemsAuthorizationCode_ReturnsAccessToken` (currently `ExtractAntiforgeryToken(...)`) to call `OidcTestHelpers.ExtractAntiforgeryToken(...)` instead.

Add the shared helper as a new top-level class in the same file, right before `OidcAuthorizationCodeGrantTests`:

```csharp
internal static class OidcTestHelpers
{
    public static string ExtractAntiforgeryToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
}

```

- [ ] **Step 2: Run the existing test to verify the extraction didn't break it**

Run: `dotnet test tests/KoalaBooks.Tests --filter OidcAuthorizationCodeGrantTests`
Expected: 1 passed (unchanged behavior, just relocated).

- [ ] **Step 3: Write the failing integration test**

In `tests/KoalaBooks.Tests/OidcTests.cs`, add these usings near the top of the file (alongside the existing ones):

```csharp
using System.Security.Cryptography;
using System.Text;
```

Then append this class at the end of the file:

```csharp
// Proves Track B of #257: the WASM client's silent authorization-code + PKCE exchange,
// driven manually here instead of by a real browser, yields an access token that both
// authenticates against the API and carries org_id for tenant scoping.
public class OidcSilentPkceForOwnClientTests
{
    [Fact]
    public async Task SilentPkceExchange_ForWasmClient_ReturnsAccessTokenWithOrgId()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            await using var factory = new WebApiFactory(connStr);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            const string email = "wasm-user@test.com";
            const string password = "ValidPass123!";
            var redirectUri = new Uri("https://localhost:7154/authentication/login-callback");
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

                await WasmClientSeeder.SeedAsync(scope.ServiceProvider, redirectUri);
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

            var codeVerifier = GenerateCodeVerifier();
            var codeChallenge = ComputeCodeChallenge(codeVerifier);

            var authorizeResponse = await client.GetAsync(
                $"/connect/authorize?client_id={WasmClientSeeder.ClientId}&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}&scope=openid%20profile%20email" +
                $"&code_challenge={codeChallenge}&code_challenge_method=S256");
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
                    ["code_verifier"] = codeVerifier,
                }));

            var body = await tokenResponse.Content.ReadAsStringAsync();
            Assert.True(tokenResponse.IsSuccessStatusCode, body);

            var json = JsonSerializer.Deserialize<JsonElement>(body);
            var accessToken = json.GetProperty("access_token").GetString()!;
            var payload = accessToken.Split('.')[1];
            var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var claimsJson = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var claims = JsonSerializer.Deserialize<JsonElement>(claimsJson);

            Assert.Equal(orgId.ToString(), claims.GetProperty("org_id").GetString());
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/KoalaBooks.Tests --filter OidcSilentPkceForOwnClientTests`
Expected (before Task 1/2 existed, this would fail; since Tasks 1-2 are already done at this point in the plan, this should instead expose whether PKCE + org_id truly work end-to-end). If it fails, the failure message (invalid_grant, invalid_request, or a null `org_id` claim) tells you which of Task 1/2's pieces to check — most likely a mismatch between the seeded `redirectUri` and the one sent in the `/connect/authorize` request.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/KoalaBooks.Tests --filter OidcSilentPkceForOwnClientTests`
Expected: 1 passed.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add tests/KoalaBooks.Tests/OidcTests.cs
git commit -m "Add end-to-end test proving silent PKCE exchange yields an org_id-bearing token"
```

---

### Task 4: Server-side Track A — serialize auth state for WASM prerender

**Files:**
- Modify: `src/KoalaBooks.Web/KoalaBooks.Web.csproj` (add package reference)
- Modify: `src/KoalaBooks.Web/Program.cs:180-182`

**Interfaces:**
- Produces: the server now persists `AuthenticationStateData` (including `org_id`, via `SerializeAllClaims = true`) into `PersistentComponentState` whenever a component in `RenderMode.InteractiveWebAssembly` is on the page. No later task calls anything from this directly — Task 6's client-side `AddAuthenticationStateDeserialization()` consumes the persisted data by convention (matching `PersistenceKey` internal to the framework), not by any API surface this task defines.

There is no automated test for this task: `AddAuthenticationStateSerialization`'s effect (`PersistentComponentState.RegisterOnPersisting` firing for `RenderMode.InteractiveWebAssembly` components) only triggers when a component actually renders in that mode, and no such component exists yet (by design — see Global Constraints). Verification is build-only.

- [ ] **Step 1: Add the package reference**

In `src/KoalaBooks.Web/KoalaBooks.Web.csproj`, add to the existing `PackageReference` `ItemGroup` (after the `Microsoft.EntityFrameworkCore.Design` entry, alphabetically before `MudBlazor`):

```xml
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.Server" Version="10.0.9" />
```

- [ ] **Step 2: Wire up serialization**

In `src/KoalaBooks.Web/Program.cs`, replace lines 180-182:

```csharp
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
```

with:

```csharp
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddAuthenticationStateSerialization(options => options.SerializeAllClaims = true);
```

- [ ] **Step 3: Build and run the full test suite**

Run: `dotnet build src/KoalaBooks.Web/KoalaBooks.Web.csproj`
Expected: build succeeds.

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: all tests still pass (this change is additive; no existing component uses WASM render mode, so nothing observable changes yet).

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Web/KoalaBooks.Web.csproj src/KoalaBooks.Web/Program.cs
git commit -m "Serialize authentication state for future WASM/Auto components"
```

---

### Task 5: Scaffold the `KoalaBooks.Client` WASM project and Track A client-side deserialization

**Files:**
- Create: `src/KoalaBooks.Client/KoalaBooks.Client.csproj`
- Create: `src/KoalaBooks.Client/Program.cs`
- Modify: `KoalaBooks.slnx` (add project entry)

**Interfaces:**
- Produces: a buildable `KoalaBooks.Client` project with no `ProjectReference` to any other KoalaBooks project, with `AddAuthenticationStateDeserialization()` wired. Task 6 adds to this same project's `Program.cs`.

No automated test: `WebAssemblyHostBuilder.CreateDefault()` performs JS interop at construction time (reading configuration from the hosting page), which only works inside an actual browser — it throws outside one, so this can't be exercised from an xunit test process. Verification is build-only, consistent with the design doc's verification plan.

**Revised during Task 5's implementation**: the original plan had this project reference `KoalaBooks.Application`. That's not possible — `KoalaBooks.Application.csproj` has a pre-existing `ProjectReference` to `KoalaBooks.Infrastructure`, which has `<FrameworkReference Include="Microsoft.AspNetCore.App" />` — a shared framework with no browser-wasm asset, so anything transitively pulling it in cannot compile for `Microsoft.NET.Sdk.BlazorWebAssembly`. Nothing in Tasks 5-6 actually uses any `Application` type, so the reference is simply dropped rather than worked around.

- [ ] **Step 1: Create the project file**

Create `src/KoalaBooks.Client/KoalaBooks.Client.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="10.0.9" />
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.Authentication" Version="10.0.9" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create `Program.cs` with Track A wiring**

Create `src/KoalaBooks.Client/Program.cs`:

```csharp
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();

await builder.Build().RunAsync();
```

- [ ] **Step 3: Add the project to the solution**

In `KoalaBooks.slnx`, add a line inside the `<Folder Name="/src/">` element (alphabetically, right after the `KoalaBooks.AppHostSupport` entry):

```xml
    <Project Path="src/KoalaBooks.Client/KoalaBooks.Client.csproj" />
```

- [ ] **Step 4: Build the new project**

Run: `dotnet build src/KoalaBooks.Client/KoalaBooks.Client.csproj`
Expected: build succeeds. The first run downloads the `browser-wasm` runtime pack via NuGet (not yet cached in this environment) — this needs network access and may take a few minutes; a restricted/offline sandbox will fail here with a restore error, which is an environment limitation, not a code defect.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Client/KoalaBooks.Client.csproj \
        src/KoalaBooks.Client/Program.cs \
        KoalaBooks.slnx
git commit -m "Scaffold KoalaBooks.Client WASM project with auth state deserialization"
```

---

### Task 6: Client-side Track B — silent OIDC renewal via the built-in remote authenticator

**Files:**
- Modify: `src/KoalaBooks.Client/Program.cs`
- Create: `src/KoalaBooks.Client/_Imports.razor`
- Create: `src/KoalaBooks.Client/Pages/Authentication.razor`

**Interfaces:**
- Consumes: `WasmClientSeeder.ClientId` value `"koalabooks-wasm"` (Task 2) as a literal string (cross-project, matching the existing `"aspire-dashboard"` literal-string convention rather than a shared constant).

As established while researching this task: a raw `fetch`-based silent exchange cannot work (manual-redirect fetch responses are opaque — the `Location` header isn't readable even same-origin), so this uses Microsoft's own generic OIDC client (`AddOidcAuthentication`/`RemoteAuthenticatorView`, part of the same `Microsoft.AspNetCore.Components.WebAssembly.Authentication` package Task 5 already added), which implements the correct hidden-iframe silent-renew technique against any standard OIDC server — including our own OpenIddict server. No automated test: exercising the actual sign-in/silent-renew flow requires a real browser and, until a future task wires this project into the Web project's routing (`AddInteractiveWebAssemblyComponents()` + `AddAdditionalAssemblies`), the route below isn't reachable by any real request. Verification is build-only.

- [ ] **Step 1: Add OIDC client registration to `Program.cs`**

In `src/KoalaBooks.Client/Program.cs`, replace:

```csharp
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();

await builder.Build().RunAsync();
```

with:

```csharp
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();

builder.Services.AddOidcAuthentication(options =>
{
    // Same-origin: the Client project is always served from the Web app's own address.
    options.ProviderOptions.Authority = builder.HostEnvironment.BaseAddress;
    options.ProviderOptions.ClientId = "koalabooks-wasm";
    options.ProviderOptions.ResponseType = "code";
    options.ProviderOptions.DefaultScopes.Add("email");
});

await builder.Build().RunAsync();
```

- [ ] **Step 2: Add `_Imports.razor`**

Create `src/KoalaBooks.Client/_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.WebAssembly.Authentication
```

- [ ] **Step 3: Add the authentication callback route**

Create `src/KoalaBooks.Client/Pages/Authentication.razor`:

```razor
@page "/authentication/{action}"

<RemoteAuthenticatorView Action="@Action" />

@code {
    [Parameter] public string? Action { get; set; }
}
```

- [ ] **Step 4: Build the project**

Run: `dotnet build src/KoalaBooks.Client/KoalaBooks.Client.csproj`
Expected: build succeeds.

- [ ] **Step 5: Run the full test suite one more time**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: all tests pass (nothing in the Web project references `KoalaBooks.Client` yet, so this is purely additive).

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Client/Program.cs \
        src/KoalaBooks.Client/_Imports.razor \
        src/KoalaBooks.Client/Pages/Authentication.razor
git commit -m "Wire silent OIDC renewal for the WASM client via the built-in remote authenticator"
```

---

## What's deliberately left for future work

- Applying `InteractiveWebAssembly`/`InteractiveAuto` render mode to any real page, which is what will actually make `KoalaBooks.Client` (and its `/authentication/{action}` route) reachable — likely after #224/#79 give RCL components clean Application-only dependencies to consume.
- Manually verifying the real browser flow (sign-in redirect, silent renew, token attached to an API call) once a real page exists to drive it — outside what an xunit process can exercise.
- Reusing this same public-client + `AddOidcAuthentication` pattern for the future MAUI client (#63), once that project moves off its current direct-Postgres/no-auth scope.
