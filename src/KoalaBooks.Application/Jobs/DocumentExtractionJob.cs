using Hangfire;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace KoalaBooks.Application.Jobs;

public class DocumentExtractionJob(
    AppDbContext db,
    IDocumentStorage storage,
    IDocumentExtractor extractor,
    ILogger<DocumentExtractionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(int documentId)
    {
        // IgnoreQueryFilters: this job has no HttpContext, so ICurrentUser.OrganisationId
        // is always null and the tenant query filter would hide every document. Safe here
        // because the job only ever acts on a documentId handed to it by trusted code that
        // just created that exact row — not arbitrary tenant-crossing input.
        var doc = await db.Documents.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return;

        var data = await storage.LoadAsync(doc.StorageKey); // storage/DB failures bubble → Hangfire retries (Attempts = 3)

        try
        {
            var result = await extractor.ExtractAsync(doc.FileName, doc.ContentType, data);
            doc.SuggestedType = result.SuggestedType;
            doc.ExtractedDataJson = result.SuggestedType is not null
                ? JsonSerializer.Serialize(result)
                : null;
            // Don't clobber a date the user already entered via the classify dialog while
            // extraction was still in flight (Bokför isn't gated on ExtractionStatus).
            if (doc.DocumentDate is null)
                doc.DocumentDate = result.InvoiceDate;
            doc.ExtractionStatus = ExtractionStatus.Completed;
        }
        catch (Exception ex)
        {
            // Content-level failure (e.g. malformed PDF) — retrying won't help, same file fails the same way.
            logger.LogWarning(ex, "Extraction failed for {FileName} — proceeds without suggestion", doc.FileName);
            doc.ExtractionStatus = ExtractionStatus.Failed;
        }

        await db.SaveChangesAsync();
    }
}
