using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class BulkJournalImportController : ControllerBase
{
    private readonly IBulkJournalImportService _bulkJournalImportService;
    private readonly IFiscalYearService _fiscalYearService;

    public BulkJournalImportController(IBulkJournalImportService bulkJournalImportService, IFiscalYearService fiscalYearService)
    {
        _bulkJournalImportService = bulkJournalImportService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpPost("fiscal-years/{fiscalYearId:int}/journal-entries/bulk-import")]
    [ProducesResponseType<BulkJournalImportResultResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Import(int fiscalYearId, [FromBody] BulkJournalImportRequest request)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var entries = request.Entries
            .Select(e => new BulkJournalEntryInput(
                e.Date, e.Description,
                e.Lines.Select(l => new BulkJournalLineInput(l.AccountId, l.DebitAmount, l.CreditAmount)).ToList()))
            .ToList();

        var result = await _bulkJournalImportService.ImportAsync(fiscalYearId, entries);
        if (!result.Success)
            return Problem(detail: result.Error, statusCode: StatusCodes.Status400BadRequest);

        return Ok(new BulkJournalImportResultResponse(result.Success, result.Error, result.FailedEntryIndex, result.CreatedEntryIds));
    }
}
