using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using KoalaBooks.Domain.Auth;

namespace KoalaBooks.Client.Services;

// Attaches a bearer token to outgoing "KoalaBooks.Api" requests, minted on demand from the
// ambient Identity cookie via the token endpoint's cookie grant (see #292 and
// KoalaBooks.Domain.Auth.WasmCookieBridge for why this exists instead of the standard
// AuthorizationMessageHandler). No refresh token is requested: the cookie session already lasts
// up to 30 days, so re-minting on expiry is just as cheap and keeps the client stateless.
public class CookieBridgeTokenHandler(IHttpClientFactory httpClientFactory) : DelegatingHandler
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _accessToken;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _accessToken;

            using var tokenClient = httpClientFactory.CreateClient("KoalaBooks.TokenBridge");
            using var request = new HttpRequestMessage(HttpMethod.Post, "connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = WasmCookieBridge.GrantType,
                    ["client_id"] = "koalabooks-wasm",
                    ["scope"] = "email profile",
                })
            };
            request.Headers.Add(WasmCookieBridge.CsrfHeaderName, WasmCookieBridge.CsrfHeaderValue);

            using var response = await tokenClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("Empty token response.");

            _accessToken = token.AccessToken;
            // Refresh a little before actual expiry so an in-flight request doesn't race it.
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(token.ExpiresIn - 30, 0));
            return _accessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
