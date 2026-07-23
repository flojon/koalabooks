using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace KoalaBooks.Web.Pages.Account;

[EnableRateLimiting("auth")]
public class MfaVerifyModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;

    public MfaVerifyModel(SignInManager<ApplicationUser> signInManager)
    {
        _signInManager = signInManager;
    }

    [BindProperty] public string Code { get; set; } = "";
    [BindProperty(SupportsGet = true)] public bool UseRecoveryCode { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public bool RememberMe { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
            return Redirect("/account/login");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
            return Redirect("/account/login");

        var normalized = Code.Replace(" ", "").Replace("-", "");

        var result = UseRecoveryCode
            ? await _signInManager.TwoFactorRecoveryCodeSignInAsync(normalized)
            : await _signInManager.TwoFactorAuthenticatorSignInAsync(normalized, RememberMe, rememberClient: false);

        if (result.Succeeded)
            return LocalRedirect(ReturnUrl ?? "/");

        if (result.IsLockedOut)
        {
            ErrorMessage = "Kontot är tillfälligt låst. Försök igen om 15 minuter.";
            return Page();
        }

        ErrorMessage = UseRecoveryCode ? "Ogiltig återställningskod." : "Fel kod. Försök igen.";
        return Page();
    }
}
