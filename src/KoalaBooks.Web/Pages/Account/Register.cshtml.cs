using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoalaBooks.Web.Pages.Account;

public class RegisterModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly AppDbContext _db;

    public RegisterModel(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        AppDbContext db)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _db = db;
    }

    [BindProperty] public string OrgName { get; set; } = "";
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";
    public List<string> Errors { get; set; } = [];

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Password != ConfirmPassword)
        {
            Errors.Add("Lösenorden matchar inte.");
            return Page();
        }

        var slug = GenerateSlug(OrgName);

        var org = new Organisation { Name = OrgName.Trim(), Slug = slug };
        _db.Organisations.Add(org);
        await _db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = Email,
            Email = Email,
            EmailConfirmed = true,
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
