using KoalaBooks.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class SieController : ControllerBase
{
    private readonly ISieExportService _sieExportService;
    private readonly IFiscalYearService _fiscalYearService;

    public SieController(ISieExportService sieExportService, IFiscalYearService fiscalYearService)
    {
        _sieExportService = sieExportService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/sie-export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Export(int fiscalYearId, [FromQuery] string? companyName = null)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var bytes = await _sieExportService.ExportAsync(fiscalYearId, companyName);
        return File(bytes, "application/octet-stream");
    }
}
