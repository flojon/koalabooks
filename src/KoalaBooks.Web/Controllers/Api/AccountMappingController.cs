using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class AccountMappingController : ControllerBase
{
    private readonly IAccountMappingService _accountMappingService;
    private readonly IFiscalYearService _fiscalYearService;

    public AccountMappingController(IAccountMappingService accountMappingService, IFiscalYearService fiscalYearService)
    {
        _accountMappingService = accountMappingService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{sourceFiscalYearId:int}/account-mapping/{targetFiscalYearId:int}")]
    [ProducesResponseType<List<MappingRowResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BuildMapping(int sourceFiscalYearId, int targetFiscalYearId)
    {
        var source = await _fiscalYearService.GetByIdAsync(sourceFiscalYearId);
        if (source is null) return NotFound();

        var target = await _fiscalYearService.GetByIdAsync(targetFiscalYearId);
        if (target is null) return NotFound();

        var rows = await _accountMappingService.BuildMappingAsync(sourceFiscalYearId, targetFiscalYearId);
        return Ok(rows.Select(MapRow).ToList());
    }

    [HttpPost("fiscal-years/{sourceFiscalYearId:int}/account-mapping/{targetFiscalYearId:int}/apply")]
    [ProducesResponseType<ApplyMappingResultResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApplyMapping(
        int sourceFiscalYearId, int targetFiscalYearId, [FromBody] ApplyMappingRequest request)
    {
        var source = await _fiscalYearService.GetByIdAsync(sourceFiscalYearId);
        if (source is null) return NotFound();

        var target = await _fiscalYearService.GetByIdAsync(targetFiscalYearId);
        if (target is null) return NotFound();

        var rows = request.Rows
            .Select(r => new MappingRow(r.SourceAccountNumber, r.SourceAccountName, r.Ub, r.TargetAccountNumber))
            .ToList();

        var result = await _accountMappingService.ApplyMappingAsync(sourceFiscalYearId, targetFiscalYearId, rows);
        return Ok(new ApplyMappingResultResponse(result.Mapped, result.Skipped));
    }

    private static MappingRowResponse MapRow(MappingRow r) =>
        new(r.SourceAccountNumber, r.SourceAccountName, r.Ub, r.TargetAccountNumber);
}
