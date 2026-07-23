using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class DocumentsController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly IBackgroundJobRunService _backgroundJobRunService;
    private readonly IJournalEntryService _journalEntryService;
    private readonly ISupplierInvoiceService _supplierInvoiceService;
    private readonly ICustomerInvoiceService _customerInvoiceService;

    public DocumentsController(
        IDocumentService documentService,
        IBackgroundJobRunService backgroundJobRunService,
        IJournalEntryService journalEntryService,
        ISupplierInvoiceService supplierInvoiceService,
        ICustomerInvoiceService customerInvoiceService)
    {
        _documentService = documentService;
        _backgroundJobRunService = backgroundJobRunService;
        _journalEntryService = journalEntryService;
        _supplierInvoiceService = supplierInvoiceService;
        _customerInvoiceService = customerInvoiceService;
    }

    [HttpGet("documents/pending")]
    [ProducesResponseType<KoalaBooks.Web.Models.Api.PagedResult<DocumentResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPending(
        [FromQuery] string? typeFilter,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string sortBy = "uploadedAt",
        [FromQuery] bool sortAsc = false,
        [FromQuery] DateOnly? fiscalYearStart = null,
        [FromQuery] DateOnly? fiscalYearEnd = null,
        [FromQuery] bool undatedOnly = false)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var docs = await _documentService.GetPendingAsync(
            typeFilter, (page - 1) * pageSize, pageSize, sortBy, sortAsc, fiscalYearStart, fiscalYearEnd, undatedOnly);
        var totalCount = await _documentService.GetPendingCountAsync(typeFilter, fiscalYearStart, fiscalYearEnd, undatedOnly);

        return Ok(new KoalaBooks.Web.Models.Api.PagedResult<DocumentResponse>
        {
            Items = docs.Select(MapDocument).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        });
    }

    [HttpGet("documents/pending-count")]
    [ProducesResponseType<CountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPendingCount(
        [FromQuery] string? typeFilter,
        [FromQuery] DateOnly? fiscalYearStart = null,
        [FromQuery] DateOnly? fiscalYearEnd = null,
        [FromQuery] bool undatedOnly = false)
    {
        var count = await _documentService.GetPendingCountAsync(typeFilter, fiscalYearStart, fiscalYearEnd, undatedOnly);
        return Ok(new CountResponse(count));
    }

    [HttpGet("documents/linked/{entityType}/{entityId:int}")]
    [ProducesResponseType<List<DocumentResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLinked(DocumentEntityType entityType, int entityId)
    {
        if (!await EntityExistsAsync(entityType, entityId)) return NotFound();

        var docs = await _documentService.GetLinkedAsync(entityType, entityId);
        return Ok(docs.Select(MapDocument).ToList());
    }

    [HttpPost("documents")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<DocumentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Upload([FromForm] UploadDocumentRequest request)
    {
        var (doc, error) = await _documentService.UploadAsync(
            request.File!.FileName, request.File.ContentType, () => request.File.OpenReadStream());
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(Download), new { id = doc!.Id }, MapDocument(doc));
    }

    [HttpPost("documents/upload-zip")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<UploadZipResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UploadZip([FromForm] UploadZipRequest request)
    {
        var (runId, error) = await _documentService.UploadZipAsync(
            request.File!.FileName, () => request.File.OpenReadStream());
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return AcceptedAtAction(nameof(GetZipImportStatus), new { runId = runId!.Value }, new UploadZipResponse(runId.Value));
    }

    [HttpGet("documents/upload-zip/{runId:int}")]
    [ProducesResponseType<BackgroundJobRunResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetZipImportStatus(int runId)
    {
        var run = await _backgroundJobRunService.GetByIdAsync(runId);
        if (run is null) return NotFound();
        return Ok(MapRun(run));
    }

    [HttpPost("documents/{id:int}/link")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Link(int id, [FromBody] LinkDocumentRequest request)
    {
        var outcome = await _documentService.LinkAsync(id, request.EntityType!.Value, request.EntityId!.Value);
        return outcome switch
        {
            LinkOutcome.Linked => NoContent(),
            LinkOutcome.ConcurrencyConflict =>
                Problem(detail: "Kunde inte länka just nu. Försök igen.", statusCode: StatusCodes.Status409Conflict),
            _ => NotFound()
        };
    }

    [HttpPut("documents/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMetadata(int id, [FromBody] UpdateDocumentRequest request)
    {
        var (found, error) = await _documentService.UpdateMetadataAsync(id, request.ClassifiedType, request.DocumentDate);
        if (!found) return NotFound();
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return NoContent();
    }

    [HttpDelete("documents/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var found = await _documentService.DeleteAsync(id);
        if (!found) return NotFound();

        return NoContent();
    }

    [HttpGet("documents/{id:int}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(int id)
    {
        var result = await _documentService.GetDownloadAsync(id);
        if (result is null) return NotFound();

        return File(result.Value.Data, result.Value.ContentType, result.Value.FileName);
    }

    private async Task<bool> EntityExistsAsync(DocumentEntityType entityType, int entityId) => entityType switch
    {
        DocumentEntityType.JournalEntry => await _journalEntryService.GetByIdAsync(entityId) is not null,
        DocumentEntityType.SupplierInvoice => await _supplierInvoiceService.GetByIdAsync(entityId) is not null,
        DocumentEntityType.CustomerInvoice => await _customerInvoiceService.GetByIdAsync(entityId) is not null,
        _ => false
    };

    private static DocumentResponse MapDocument(Document d) =>
        new(d.Id, d.FileName, d.ContentType, d.FileSize, d.UploadedAt,
            d.ClassifiedType, d.SuggestedType, d.ExtractedDataJson, d.DocumentDate, d.ExtractionStatus);

    private static DocumentResponse MapDocument(DocumentMeta m) =>
        new(m.Id, m.FileName, m.ContentType, m.FileSize, m.UploadedAt,
            m.ClassifiedType, m.SuggestedType, m.ExtractedDataJson, m.DocumentDate, m.ExtractionStatus);

    private static BackgroundJobRunResponse MapRun(BackgroundJobRun r) =>
        new(r.Id, r.JobType, r.Status, r.ProcessedCount, r.TotalCount, r.ResultJson, r.Acknowledged, r.CreatedAt);
}
