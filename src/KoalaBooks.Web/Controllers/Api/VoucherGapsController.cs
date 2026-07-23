using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class VoucherGapsController : ControllerBase
{
    private readonly IVoucherGapService _voucherGapService;
    private readonly IFiscalYearService _fiscalYearService;

    public VoucherGapsController(IVoucherGapService voucherGapService, IFiscalYearService fiscalYearService)
    {
        _voucherGapService = voucherGapService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/voucher-gaps")]
    [ProducesResponseType<List<int>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGaps(int fiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var gaps = await _voucherGapService.FindGapsAsync(fiscalYearId);
        return Ok(gaps);
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/voucher-gaps/unexplained")]
    [ProducesResponseType<List<int>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUnexplainedGaps(int fiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var gaps = await _voucherGapService.GetUnexplainedGapsAsync(fiscalYearId);
        return Ok(gaps);
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/voucher-gaps/explanations")]
    [ProducesResponseType<List<VoucherGapExplanationResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetExplanations(int fiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var explanations = await _voucherGapService.GetExplanationsAsync(fiscalYearId);
        return Ok(explanations.Select(MapExplanation).ToList());
    }

    [HttpPost("fiscal-years/{fiscalYearId:int}/voucher-gaps/explanations")]
    [ProducesResponseType<List<VoucherGapExplanationResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddExplanation(int fiscalYearId, [FromBody] AddVoucherGapExplanationRequest request)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var error = await _voucherGapService.AddExplanationAsync(
            fiscalYearId, request.MissingEntryNumber!.Value, request.Explanation, request.ExplainedBy);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        var explanations = await _voucherGapService.GetExplanationsAsync(fiscalYearId);
        return Ok(explanations.Select(MapExplanation).ToList());
    }

    private static VoucherGapExplanationResponse MapExplanation(VoucherGapExplanation e) =>
        new(e.Id, e.FiscalYearId, e.MissingEntryNumber, e.Explanation, e.ExplainedAt, e.ExplainedBy);
}
