using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenIddict.Abstractions;
using Microsoft.AspNetCore;
using OpenIddict.Server.AspNetCore;
using System.Security.Claims;

namespace KoalaBooks.Web.Pages.Connect;

[IgnoreAntiforgeryToken]
public class AuthorizeModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;

    public AuthorizeModel(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public Task<IActionResult> OnGetAsync() => HandleAsync();
    public Task<IActionResult> OnPostAsync() => HandleAsync();

    private async Task<IActionResult> HandleAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");

        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded)
        {
            return Challenge(
                authenticationSchemes: IdentityConstants.ApplicationScheme,
                properties: new AuthenticationProperties
                {
                    RedirectUri = Request.PathBase + Request.Path +
                        QueryString.Create(Request.HasFormContentType
                            ? [.. Request.Form]
                            : [.. Request.Query])
                });
        }

        var user = await _userManager.GetUserAsync(result.Principal)
            ?? throw new InvalidOperationException("The authenticated user could not be found.");

        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity.SetClaim(OpenIddictConstants.Claims.Subject, await _userManager.GetUserIdAsync(user))
                .SetClaim(OpenIddictConstants.Claims.Email, user.Email)
                .SetClaim(OpenIddictConstants.Claims.Name, user.DisplayName ?? user.Email);

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
