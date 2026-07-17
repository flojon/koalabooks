using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoalaBooks.Web.Pages.FiscalYear;

// Plain HTTP form target (like Account/Logout) rather than a Blazor event handler:
// Blazor Server can't set response cookies once the SignalR circuit is established.
[Authorize]
public class SelectModel : PageModel
{
    private readonly IFiscalYearService _fiscalYearService;
    private readonly ICurrentFiscalYearContext _fiscalYearContext;

    public SelectModel(IFiscalYearService fiscalYearService, ICurrentFiscalYearContext fiscalYearContext)
    {
        _fiscalYearService = fiscalYearService;
        _fiscalYearContext = fiscalYearContext;
    }

    public async Task<IActionResult> OnPostAsync(int? fiscalYearId, string? returnUrl)
    {
        if (fiscalYearId is null)
        {
            _fiscalYearContext.SetSelectedFiscalYear(null);
        }
        else
        {
            var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId.Value);
            if (fy is not null && !fy.IsClosed)
            {
                _fiscalYearContext.SetSelectedFiscalYear(fy.Id);
            }
        }

        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
