# Auth bridge for a browser-hosted WASM/Auto client (#257)

## Background

Blazor Server today relies entirely on the SignalR circuit plus server-side
cookie auth (ASP.NET Identity + OpenIddict). `Program.cs` registers only
`AddInteractiveServerComponents()` / `AddInteractiveServerRenderMode()` — no
`InteractiveWebAssembly` or `InteractiveAuto` render mode exists anywhere in
the app, and there is no `Client` (WASM) project. This is greenfield.

Moving any component to a WASM-executed render mode breaks the "auth just
works" assumption: WASM runs in the browser sandbox, so it needs its own way
to (a) know who's logged in for UI purposes, and (b) attach a real
credential to HTTP calls against the OpenIddict-protected API.

The existing password-grant flow (`Pages/Connect/Token.cshtml.cs`, added for
REST API v1 / #11) is the wrong shape for this: it's for third-party/script
clients holding a username+password, not our own browser code that already
has an authenticated session.

## Goal

Design (not yet build a real feature page for) the plumbing that lets a
future `InteractiveWebAssembly`/`InteractiveAuto` component:

1. See the same `AuthenticationState` server components see, with no
   duplicate login UI.
2. Make HTTP calls to the API that pass the existing OpenIddict validation
   pipeline and `org_id` tenant scoping, without ever holding or exposing the
   user's password.

This issue also underpins the future MAUI client (#63), which will
eventually face the same "authenticate my own HTTP calls" problem — though
#63 as currently scoped uses a direct Postgres connection with no auth, so
that reuse is a future concern, not part of this design.

## Non-goals

- No real `InteractiveWebAssembly`/`InteractiveAuto` page is added in this
  work. There is no concrete feature ready to move yet, and #224/#79
  (extracting Application-layer interfaces so RCL components don't depend on
  Infrastructure) are still open — building a real WASM page now would
  either violate that boundary or block on unrelated refactors. This design
  proves the bridge end-to-end via a `WebApplicationFactory`-based
  integration test instead of a live browser render.
- Not building a BFF-style proxy (server forwards WASM's calls using the
  cookie, WASM never sees a token). Rejected because the issue explicitly
  wants WASM's calls validated by the *existing* OpenIddict bearer pipeline,
  and because the same mechanism needs to generalize to MAUI later, which
  has no server-side cookie to forward.
- Not adding a new OpenIddict grant type. Everything rides the
  already-configured `/connect/authorize` + `/connect/token` endpoints.
- Not solving MAUI's auth story (#63). Related, deliberately deferred.

## Design

Two complementary mechanisms, answering the two different questions above.
They are not alternatives to each other — the issue frames
`PersistentAuthenticationStateProvider` vs. authorization-code+PKCE as an
either/or choice, but they solve different problems and this design uses
both.

### Track A — "who's logged in", for UI (`AuthenticationState`)

Use the built-in ASP.NET Core 8+ mechanism, rather than hand-rolling a
`PersistentAuthenticationStateProvider`:

- Server (`Program.cs`): `AddRazorComponents().AddInteractiveServerComponents().AddAuthenticationStateSerialization(...)`.
  Configure `AuthenticationStateSerializationOptions` to include the `org_id`
  claim explicitly (the default only serializes name/role claims) — WASM
  components that branch UI on tenant identity need it available without a
  round trip.
- New `KoalaBooks.Client` project (WASM host, referencing only
  `KoalaBooks.Application` — no `Infrastructure`, matching the direction
  #79 is already pushing the RCL toward): `Program.cs` calls
  `AddAuthenticationStateDeserialization()`. This automatically wires an
  `AuthenticationStateProvider` that deserializes the state the server
  persisted via `PersistentComponentState` at prerender — no custom
  provider class needed, no duplicate login UI, `AuthorizeView` /
  `CascadingAuthenticationState` work unchanged.

This is prerender-time only: it gives WASM a claims snapshot as of the page
load. It does not, by itself, give WASM anything it can present to the API
as a bearer credential — that's Track B.

Note this claims snapshot is technically redundant with Track B once Track
B's silent exchange completes, since the resulting access token carries the
same claims and could drive `AuthenticationState` on its own. Track A is
kept anyway for the first paint: without it, a WASM component would render
in an unauthenticated/loading state for the duration of Track B's
redirect+POST round trip on boot. Track A removes that flicker at the cost
of a second, separate claims-serialization mechanism to maintain.

### Track B — authenticating outbound API calls from WASM

Drive the already-configured OpenIddict authorization-code + PKCE flow
silently — same-origin `fetch`, the existing auth cookie sent automatically,
no visible redirect or login prompt, because the user is already
authenticated:

1. Register a new **public** OpenIddict client application at startup,
   following the existing `AspireDashboardSeeder` pattern (a new
   `WasmClientSeeder` or similar): `ClientType = Public` (no secret, since
   it runs in the browser), PKCE required, `GrantTypes.AuthorizationCode` +
   `GrantTypes.RefreshToken`, redirect URI pointing at a same-origin
   callback route.
2. On first API call (or when the cached token is expired), the WASM
   `HttpClient`'s outgoing message handler:
   - Generates a PKCE `code_verifier`/`code_challenge` pair.
   - Fetches `/connect/authorize?...&code_challenge=...` with
     `credentials: 'include'`. Because the OpenIddict cookie session is
     already valid, `Authorize.cshtml.cs` signs the code straight through
     with no consent/login screen (same as it does today for
     `aspire-dashboard`).
   - Captures the `code` from the redirect and posts it to `/connect/token`
     with the verifier to exchange it for an access + refresh token.
   - Caches both **in memory only** (not `localStorage`/`sessionStorage`) to
     avoid XSS-exfiltration exposure; a full page reload re-runs the silent
     exchange, which is cheap and invisible to the user.
   - Attaches `Authorization: Bearer <token>` to the actual request, and
     transparently uses the refresh token to renew when the access token is
     close to expiry.
3. **Fix a real gap surfaced by this design**: `Authorize.cshtml.cs` builds
   its `ClaimsIdentity` without the `org_id` claim that
   `Token.cshtml.cs`'s password-grant branch sets. Tokens minted via the
   authorization-code flow would silently fail tenant scoping without this.
   Extract the "build identity + set org_id + set destinations" logic
   `Token.cshtml.cs` already has into a small shared helper and call it from
   both pages, so the two flows can't drift again.

### Testing

Add an integration test alongside `tests/KoalaBooks.Tests/Api/ApiTests.cs`,
reusing its `WebApiFactory`/`PostgresContainerFixture` fixtures:

- Log in via the cookie scheme (form login, as a real browser session
  would), then drive the same silent PKCE exchange the WASM handler would
  perform: hit `/connect/authorize` with a generated challenge, follow the
  redirect to capture `code`, POST it to `/connect/token`, extract the
  access token.
- Call an existing `[Authorize]` API endpoint with that token and assert a
  200 plus correctly `org_id`-scoped data — mirroring
  `AuthenticatedClientAsync`'s assertions but sourced from the code+PKCE
  path instead of the password grant, proving Track B end-to-end without
  needing a real browser or Playwright.
- A narrower test on `Authorize.cshtml.cs` confirming the fixed identity
  now carries `org_id` with the correct access-token destination.

This satisfies the issue's ask for "an integration test analogous to
`ApiTests.cs`" without requiring an actual rendered WASM page, consistent
with the non-goals above.

## Out of scope / follow-ups

- Applying `InteractiveWebAssembly`/`InteractiveAuto` to a real page —
  future work, likely after #224/#79 give RCL components clean
  Application-only dependencies to consume from `KoalaBooks.Client`.
- Reusing Track B's silent PKCE exchange for MAUI (#63) once that project
  moves off direct-Postgres-no-auth — the same public client + flow should
  work unchanged since MAUI can also drive `/connect/authorize` +
  `/connect/token` itself (no cookie needed there; MAUI would do an
  interactive one-time system-browser login instead of a silent one, then
  cache the refresh token).
- Multi-tab token coordination — each WASM instance keeps its own in-memory
  token; not a problem today since there's no real page using it yet, worth
  revisiting once one exists.

## Verification plan

1. `dotnet build` the solution with the new `KoalaBooks.Client` project
   added — confirms it compiles and stays out of the request path (no page
   references it yet, so nothing changes for existing users).
2. Run the new integration test; confirm it exercises the full
   authorize→code→token→authenticated-API-call chain and fails if `org_id`
   is missing (regression-proves the `Authorize.cshtml.cs` fix).
3. Run the full existing test suite to confirm the shared claims-building
   helper didn't change password-grant (`Token.cshtml.cs`) behavior.
