using KoalaBooks.Domain.Auth;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;
using Microsoft.AspNetCore;
using OpenIddict.Server.AspNetCore;
using System.Security.Claims;

namespace KoalaBooks.Web.Pages.Connect;

[EnableRateLimiting("auth")]
[IgnoreAntiforgeryToken]
public class TokenModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public TokenModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    public Task<IActionResult> OnPostAsync() => HandleAsync();

    private async Task<IActionResult> HandleAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");

        // Redeems the code issued in Authorize.cshtml.cs, reusing the principal
        // attached to it by the OpenIddict server middleware (e.g. the Aspire dashboard's OIDC login).
        if (request.IsAuthorizationCodeGrantType())
        {
            var codeResult = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            return SignIn(codeResult.Principal!, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        // #292: bridges the WASM client's ambient Identity cookie session to a bearer token,
        // without going through AddOidcAuthentication()'s RemoteAuthenticationService (which
        // conflicts with AddAuthenticationStateDeserialization() over the AuthenticationStateProvider
        // DI slot).
        if (request.GrantType == WasmCookieBridge.GrantType)
            return await HandleCookieGrantAsync(request);

        if (request.GrantType != OpenIddictConstants.GrantTypes.Password)
            return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        var user = await _userManager.FindByNameAsync(request.Username ?? "");
        if (user is null)
            return Forbid(
                authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The credentials are invalid."
                }));

        var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password ?? "", lockoutOnFailure: true);
        if (!result.Succeeded)
            return Forbid(
                authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = result.IsLockedOut
                        ? "The account is locked out."
                        : result.IsNotAllowed
                            ? "The account is not confirmed."
                            : "The credentials are invalid."
                }));

        var principal = OpenIddictIdentityBuilder.BuildPrincipal(
            user, await _userManager.GetUserIdAsync(user), request.GetScopes());

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private async Task<IActionResult> HandleCookieGrantAsync(OpenIddictRequest request)
    {
        if (Request.Headers[WasmCookieBridge.CsrfHeaderName] != WasmCookieBridge.CsrfHeaderValue)
            return Forbid(
                authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidRequest,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "Missing required header."
                }));

        var cookieResult = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!cookieResult.Succeeded)
            return Forbid(
                authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "No active session."
                }));

        var user = await _userManager.GetUserAsync(cookieResult.Principal)
            ?? throw new InvalidOperationException("The authenticated user could not be found.");

        var principal = OpenIddictIdentityBuilder.BuildPrincipal(
            user, await _userManager.GetUserIdAsync(user), request.GetScopes());

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
