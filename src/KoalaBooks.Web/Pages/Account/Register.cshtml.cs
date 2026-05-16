using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace KoalaBooks.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("auth")]
public class RegisterModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public RegisterModel(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        AppDbContext db,
        IConfiguration config)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _db = db;
        _config = config;
    }

    private const string RegistrationEnabledKey = "Features:RegistrationEnabled";

    [BindProperty] public string OrgName { get; set; } = "";
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";
    public List<string> Errors { get; set; } = [];

    public IActionResult OnGet()
    {
        if (!_config.GetValue<bool>(RegistrationEnabledKey, true))
            return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!_config.GetValue<bool>(RegistrationEnabledKey, true))
            return NotFound();

        if (string.IsNullOrWhiteSpace(OrgName))
        {
            Errors.Add("Organisationsnamn får inte vara tomt.");
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Email))
        {
            Errors.Add("E-postadress får inte vara tom.");
            return Page();
        }

        if (Password != ConfirmPassword)
        {
            Errors.Add("Lösenorden matchar inte.");
            return Page();
        }

        var orgName = OrgName.Trim();
        var slug = GenerateSlug(orgName);

        var org = new Organisation { Name = orgName, Slug = slug };
        _db.Organisations.Add(org);
        await _db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = Email,
            Email = Email,
            OrganisationId = org.Id
        };

        var result = await _userManager.CreateAsync(user, Password);
        if (!result.Succeeded)
        {
            _db.Organisations.Remove(org);
            await _db.SaveChangesAsync();
            Errors.AddRange(result.Errors.Select(e => e.Description));
            return Page();
        }

        await _signInManager.SignInAsync(user, isPersistent: false);
        return Redirect("/");
    }

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("å", "a").Replace("ä", "a").Replace("ö", "o")
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            .Aggregate("", (s, c) => s + c)
            .Trim('-');

        return $"{slug}-{Guid.NewGuid().ToString("N")[..8]}";
    }
}
