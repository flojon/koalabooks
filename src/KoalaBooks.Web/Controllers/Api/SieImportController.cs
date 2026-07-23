using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

// Async SIE import, per program plan 5.H-1 — subsumes issue #279. Preview stays
// synchronous (fast, read-only parse); import runs through Hangfire via SieImportJob
// because it can write a large number of accounts/entries, and the status is polled back
// via the shared BackgroundJobRun envelope (same shape Agent F's upload-zip endpoint uses).
[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1/sie")]
public class SieImportController : ControllerBase
{
    private readonly ISieImportUploadService _sieImportUploadService;
    private readonly IBackgroundJobRunService _backgroundJobRunService;

    public SieImportController(ISieImportUploadService sieImportUploadService, IBackgroundJobRunService backgroundJobRunService)
    {
        _sieImportUploadService = sieImportUploadService;
        _backgroundJobRunService = backgroundJobRunService;
    }

    [HttpPost("preview")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<SieImportPreviewResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Preview([FromForm] UploadSieRequest request)
    {
        await using var stream = request.File!.OpenReadStream();
        var (preview, error) = await _sieImportUploadService.PreviewAsync(stream);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return Ok(MapPreview(preview!));
    }

    [HttpPost("import")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<SieImportEnqueuedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Import([FromForm] ImportSieRequest request)
    {
        var (runId, error) = await _sieImportUploadService.EnqueueImportAsync(
            request.File!.FileName, () => request.File.OpenReadStream(), request.Overwrite, request.RarId);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return AcceptedAtAction(nameof(GetImportStatus), new { runId = runId!.Value }, new SieImportEnqueuedResponse(runId.Value));
    }

    [HttpGet("import/{runId:int}")]
    [ProducesResponseType<BackgroundJobRunResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetImportStatus(int runId)
    {
        var run = await _backgroundJobRunService.GetByIdAsync(runId);
        if (run is null) return NotFound();
        return Ok(MapRun(run));
    }

    private static SieImportPreviewResponse MapPreview(SieImportPreview p) =>
        new(p.CompanyName, p.OrgNumber, p.SieType,
            p.FiscalYears.Select(f => new SieImportFiscalYearResponse(
                f.RarId, f.Start, f.End, f.Label, f.VoucherCount, f.BalanceCount, f.ExistsInDatabase, f.ExistingFiscalYearId)).ToList(),
            p.AccountCount, p.VoucherCount);

    private static BackgroundJobRunResponse MapRun(BackgroundJobRun r) =>
        new(r.Id, r.JobType, r.Status, r.ProcessedCount, r.TotalCount, r.ResultJson, r.Acknowledged, r.CreatedAt);
}
