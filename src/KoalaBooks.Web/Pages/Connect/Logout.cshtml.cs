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
