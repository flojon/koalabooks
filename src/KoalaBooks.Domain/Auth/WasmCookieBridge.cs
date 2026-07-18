namespace KoalaBooks.Domain.Auth;

// Shared between KoalaBooks.Web (Pages/Connect/Token.cshtml.cs), KoalaBooks.Infrastructure
// (Services/WasmClientSeeder.cs) and KoalaBooks.Client (Services/CookieBridgeTokenHandler.cs):
// the custom OAuth grant type that lets a WASM client already holding the server's Identity
// cookie mint a bearer token for calling the bearer-only API, without going through
// AddOidcAuthentication()'s RemoteAuthenticationService (which conflicts with
// AddAuthenticationStateDeserialization() over the AuthenticationStateProvider DI slot - see #292).
public static class WasmCookieBridge
{
    public const string GrantType = "urn:koalabooks:grant-type:cookie";

    // Cross-site POSTs already lose the SameSite=Lax cookie, but this header is cheap
    // defense-in-depth: it forces a CORS preflight for any cross-origin caller, and simple
    // (non-JS) form/img/script vectors can't set custom headers at all.
    public const string CsrfHeaderName = "X-KoalaBooks-Csrf";
    public const string CsrfHeaderValue = "1";
}
