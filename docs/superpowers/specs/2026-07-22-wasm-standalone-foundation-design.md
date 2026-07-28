# WASM standalone migration — Sub-project 1: hosting split + auth rework

Tracking issue: #344 (WASM standalone migration, supersedes the Auto approach from #256).

## Background

The Blazor Auto PoC (#256, PR #291/#318) proved the client-side API-service pattern works for one
page (`/review`), but kept `KoalaBooks.Web` as a Blazor Server host with per-page `@rendermode`
opt-in. The decision (2026-07-22) is to skip Auto and go straight to a standalone
`InteractiveWebAssembly` SPA: `KoalaBooks.Web` becomes a pure API/auth-server backend, static-hostable
with no Blazor Server circuit or SignalR dependency, and `KoalaBooks.Client` becomes the only UI.

This is the first of four sequential sub-projects tracked under #344. Nothing else in that plan
(shared shell rework, per-resource page conversion) can be sanely designed until this lands, because
every other sub-project assumes the client is a real standalone SPA with working auth.

## Current state (verified against `origin/main`, not local cache — see lesson in
[[feedback_fetch_before_worktree]])

- `KoalaBooks.Web/Program.cs` registers both `AddInteractiveServerComponents()` and
  `AddInteractiveWebAssemblyComponents()`; `App.razor` defaults every page to `InteractiveServer`
  except `/review`, which opts into `InteractiveAuto`.
- Auth for the WASM side is a custom bridge: `CookieBridgeTokenHandler` (in
  `KoalaBooks.Client/Services`) exchanges the ambient ASP.NET Identity cookie for a bearer token via
  a custom OAuth grant (`WasmCookieBridge.GrantType`), registered on the `koalabooks-wasm` OpenIddict
  client (`WasmClientSeeder`). This exists specifically because `AddOidcAuthentication()`'s
  `RemoteAuthenticationService` claims the same `AuthenticationStateProvider` DI slot as
  `AddAuthenticationStateDeserialization()`, which the Auto approach needed to reflect the
  server-persisted login without a second sign-in (#292).
- Login/logout/register are Razor Pages (not Blazor Components) at
  `KoalaBooks.Web/Pages/Account/{Login,Logout,Register}.cshtml`, backed by
  `AddIdentity<ApplicationUser, IdentityRole>()` + cookie auth (`options.LoginPath = "/account/login"`).
  These are intrinsic to OpenIddict being an authorization server — the authorization endpoint
  (`/connect/authorize`) needs a login form to challenge an unauthenticated browser, independent of
  whatever render mode the application pages use.
- `KoalaBooks.Client` already exists as a `Microsoft.NET.Sdk.BlazorWebAssembly` project with client-side
  API services for 8 resources (Account, FiscalYear, JournalEntry, BankImport, SupplierInvoice,
  Customer, CustomerInvoice, SieExport), but only `/review` sets `@rendermode`.

## Target architecture

- **`KoalaBooks.Web`**: REST API controllers + OpenIddict authorization server (incl. its existing
  Login/Logout/Register Razor Pages) + Hangfire dashboard. All Blazor Components hosting removed:
  no `AddInteractiveServerComponents()`/`AddInteractiveWebAssemblyComponents()`, no
  `MapRazorComponents<App>()` or its `AddInteractiveServerRenderMode()`/
  `AddInteractiveWebAssemblyRenderMode()` chain, `App.razor`/`Routes.razor` deleted, the
  `KoalaBooks.Components` project reference for page hosting dropped (any shared non-page code it
  still holds moves or gets referenced directly by `KoalaBooks.Client`).
- **`KoalaBooks.Client`**: sole UI, published as static files (`dotnet publish` → `wwwroot`
  output), served by the existing Caddy instance (same one used for prod + PR previews, see
  [[reference_pr_preview_infra]]) on the same origin as the API, with Caddy reverse-proxying
  `/api/*` and `/connect/*` to `KoalaBooks.Web` and serving everything else as static files
  (with SPA fallback to `index.html` for client-side routing).
- **Auth**: standard `AddOidcAuthentication()` (Microsoft.AspNetCore.Components.WebAssembly.Authentication),
  authorization-code + PKCE flow against the same-origin OpenIddict authority. Tokens held in
  `sessionStorage` via the library's `oidc-client-js` defaults, with iframe silent-renew
  (`prompt=none` against `/connect/authorize`) — no refresh-token grant, matching the "standard
  defaults" choice made for this sub-project (session ends when the tab/browser is closed; longer
  sessions are an explicit non-goal here).

## Changes by project

### `KoalaBooks.Web`
- Remove: `AddInteractiveServerComponents()`, `AddInteractiveWebAssemblyComponents()`,
  `MapRazorComponents<App>()` chain, `App.razor`, `Routes.razor`, the
  `AddAuthenticationStateSerialization()` call, `KoalaBooks.Components` project reference (page
  hosting only — verify nothing else in `Program.cs` needs it before dropping).
- Keep: `AddIdentity<...>()`, cookie auth config, `AddOpenIddict()`, `AddRazorPages()` (for
  Login/Logout/Register), all `Controllers/Api/*`, Hangfire registration/dashboard mapping.
- `WasmClientSeeder`: change `koalabooks-wasm` client's `Permissions` from
  `GrantTypes.Prefixes.GrantType + WasmCookieBridge.GrantType` to
  `Permissions.GrantTypes.AuthorizationCode`, `Permissions.ResponseTypes.Code`,
  `Permissions.Endpoints.Authorization`, plus explicit `RedirectUris`/`PostLogoutRedirectUris`
  pointing at the same-origin `authentication/login-callback` and `authentication/logout-callback`
  paths, and `Requirements.Features.ProofKeyForCodeExchange`.
- Delete `CookieBridgeTokenHandler`, `WasmCookieBridge` (the custom grant handler on the server
  side), and the custom grant-type OpenIddict server option (`.AllowCustomFlow(...)`) once nothing
  references it.
- CORS: none needed — same-origin per the confirmed hosting topology.

### `KoalaBooks.Client`
- `Program.cs`: replace the `CookieBridgeTokenHandler`/`AddHttpClient("KoalaBooks.TokenBridge", ...)`/
  `AddAuthenticationStateDeserialization()` block with `builder.Services.AddOidcAuthentication(options
  => { options.ProviderOptions.Authority = builder.HostEnvironment.BaseAddress; ...
  options.ProviderOptions.ClientId = "koalabooks-wasm"; ... })`, keeping the same-origin
  `"KoalaBooks.Api"` named `HttpClient` but attaching the standard `AuthorizationMessageHandler`
  instead of `CookieBridgeTokenHandler`.
- Add the standard `RemoteAuthenticatorView`-hosting route (typically `Pages/Authentication.razor` at
  `authentication/{action}`) so the login/logout-callback redirects have somewhere to land.
- `wwwroot/index.html` (new, since this becomes the actual host page — currently `App.razor`/Server
  host page fills this role): SPA host page with the WASM boot script, replacing what `App.razor`
  did for script/style includes (`download.js`, MudBlazor CSS, etc.).

### Deployment (Caddy)
- Update the Caddyfile (prod + PR-preview templates, per [[reference_pr_preview_infra]]) to serve
  `KoalaBooks.Client`'s published output as static files with SPA fallback, and reverse-proxy
  `/api/*` + `/connect/*` to the `KoalaBooks.Web` container. Exact path/container wiring to be
  worked out against the existing compose files during implementation — this design fixes the
  *shape* (one Caddy, one origin, static + proxy split), not every directive.

## Explicitly out of scope for this sub-project

- Converting any page beyond whatever's needed to prove the auth flow end-to-end (`/review` already
  works; this sub-project just needs it to keep working under the new auth, not add new pages).
- `MainLayout`'s nav-badge `IServiceScopeFactory` pattern — tracked as sub-project 2.
- Any new client-side API services (Documents/Inbox, Organisation, SieImport, Reports,
  YearEndClosing, AccountMapping, VoucherGaps, Todo count) — tracked as sub-project 4.

## Testing / verification plan

- `dotnet build` both projects clean (0 warnings/errors).
- Existing integration test suite (`WebApiFactory` + Testcontainers) continues to pass unmodified —
  it exercises `KoalaBooks.Web`'s API surface directly and shouldn't care about Components hosting
  being removed, but must be re-run to confirm no hidden coupling.
- `tests/KoalaBooks.Tests/OidcTests.cs` asserts the *current* `WasmClientSeeder.ClientId` cookie-bridge
  grant flow (client creation, `WasmCookieBridge.GrantType` token exchange) — these tests must be
  rewritten to exercise the new `AuthorizationCode`/PKCE permissions instead of deleted outright,
  since they're the only automated coverage of the OpenIddict client config.
- Local/dev pass: run both projects behind the updated Caddy config (or Aspire, if it's used for
  local dev — check `KoalaBooks.AppHost`), confirm static files and proxied API paths both resolve
  correctly on the same origin.
- Manual browser pass of the real OIDC flow end-to-end: hit a protected route while logged out →
  redirected to `/connect/authorize` → `Login.cshtml` challenge → redirected back with a code →
  `authentication/login-callback` exchanges it → protected route renders with real data. Then
  logout → session cleared → protected route redirects to login again.
- Confirm `/review` (today's only WASM-rendered page) still works end-to-end under the new auth,
  since it's the one existing proof-point this migration must not regress.
