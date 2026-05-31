using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using System.Security.Claims;
using Microsoft.AspNetCore;

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

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
