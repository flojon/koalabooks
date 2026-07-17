using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Web.Services;

/// <summary>
/// Backed by a plain (non-auth) cookie rather than in-memory scoped state: Blazor Server's
/// MainLayout spins up its own DI scopes for badge counts (see MainLayout.razor), so a
/// scoped field would not be visible there. Reading live from HttpContext on every access
/// mirrors HttpContextCurrentUser and keeps the value consistent across those scopes.
/// </summary>
public class CookieCurrentFiscalYearContext : ICurrentFiscalYearContext
{
    private const string CookieName = "kb_fiscal_year";
    private readonly IHttpContextAccessor _accessor;

    public CookieCurrentFiscalYearContext(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public int? SelectedFiscalYearId
    {
        get
        {
            var value = _accessor.HttpContext?.Request.Cookies[CookieName];
            return int.TryParse(value, out var id) ? id : null;
        }
    }

    public void SetSelectedFiscalYear(int? fiscalYearId)
    {
        var context = _accessor.HttpContext;
        if (context is null) return;

        if (fiscalYearId is null)
        {
            context.Response.Cookies.Delete(CookieName);
        }
        else
        {
            context.Response.Cookies.Append(CookieName, fiscalYearId.Value.ToString(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true
            });
        }
    }
}
