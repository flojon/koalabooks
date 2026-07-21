using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace KoalaBooks.Web.Pages.Account;

[EnableRateLimiting("auth")]
public class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;

    public LoginModel(SignInManager<ApplicationUser> signInManager)
    {
        _signInManager = signInManager;
    }

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var result = await _signInManager.PasswordSignInAsync(Email, Password, RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
            return LocalRedirect(ReturnUrl ?? "/");

        if (result.RequiresTwoFactor)
            return Redirect($"/account/mfa/verify?returnUrl={Uri.EscapeDataString(ReturnUrl ?? "/")}&rememberMe={RememberMe}");

        ErrorMessage = result.IsLockedOut
            ? "Kontot är tillfälligt låst. Försök igen om 15 minuter."
            : "Felaktig e-postadress eller lösenord.";
        return Page();
    }
}
