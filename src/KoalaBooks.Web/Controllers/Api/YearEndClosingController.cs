using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

// Nested under fiscal-years/{id}/year-end-closing to match FiscalYears.razor's existing UI
// pattern of treating closing as a fiscal-year action (see program plan 5.B). Not a
// FiscalYearsController action — IYearEndClosingService's validate/preview/execute triad is
// its own resource, owned by this controller.
[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class YearEndClosingController : ControllerBase
{
    private readonly IYearEndClosingService _yearEndClosingService;
    private readonly IFiscalYearService _fiscalYearService;

    public YearEndClosingController(IYearEndClosingService yearEndClosingService, IFiscalYearService fiscalYearService)
    {
        _yearEndClosingService = yearEndClosingService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/year-end-closing/validate")]
    [ProducesResponseType<ClosingValidationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Validate(int fiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var result = await _yearEndClosingService.ValidateForClosingAsync(fiscalYearId);
        return Ok(new ClosingValidationResponse(result.IsValid, result.Errors));
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/year-end-closing/preview")]
    [ProducesResponseType<ClosingPreviewResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Preview(int fiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var preview = await _yearEndClosingService.PreviewClosingAsync(fiscalYearId);
        return Ok(new ClosingPreviewResponse(
            preview.IsValid,
            preview.Errors,
            preview.TotalRevenue,
            preview.TotalExpenses,
            preview.NetResult,
            preview.Entries
                .Select(e => new ClosingEntryPreviewResponse(
                    e.Description,
                    e.Lines.Select(l => new ClosingLinePreviewResponse(l.AccountNumber, l.AccountName, l.Debit, l.Credit)).ToList()))
                .ToList()));
    }

    [HttpPost("fiscal-years/{fiscalYearId:int}/year-end-closing/execute")]
    [ProducesResponseType<ClosingResultResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Execute(int fiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var result = await _yearEndClosingService.ExecuteClosingAsync(fiscalYearId);
        if (!result.Success)
            return Problem(detail: result.Error, statusCode: StatusCodes.Status400BadRequest);

        return Ok(new ClosingResultResponse(result.Success, result.Error, result.ClosingEntry1Number, result.ClosingEntry2Number));
    }
}
