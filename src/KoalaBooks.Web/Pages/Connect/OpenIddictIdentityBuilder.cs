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
