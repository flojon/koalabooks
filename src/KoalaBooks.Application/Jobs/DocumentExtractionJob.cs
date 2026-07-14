using Hangfire;
using KoalaBooks.Domain.Entities;
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
            // extraction was still in flight (Bokför isn't gated on ExtractionStatus). This
            // check alone only catches the case where the write already landed before we
            // loaded doc above — SaveChangesResolvingConcurrencyAsync below closes the rest
            // of the window (a write landing during the extractor.ExtractAsync call itself).
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

        await SaveChangesResolvingConcurrencyAsync(doc);
    }

    // Document.Xmin (Postgres' native row-version column) is a concurrency token, so a
    // write that landed on this row between our read above and this save — most notably
    // the user classifying the document via "Bokför" while extraction was in flight —
    // raises DbUpdateConcurrencyException instead of being silently overwritten. Resolve
    // it by deferring to whatever DocumentDate is in the database now (if any), then retry
    // once against the current row version.
    private async Task SaveChangesResolvingConcurrencyAsync(Document doc)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // ex.Entries yields the non-generic EntityEntry wrapper even though the
            // tracked instance is a Document — EntityEntry<T> is never what's reported here.
            var entry = ex.Entries.Single();
            var databaseValues = await entry.GetDatabaseValuesAsync();
            if (databaseValues is null)
            {
                // The document was deleted concurrently — nothing left to update.
                return;
            }

            var dbDate = (DateOnly?)databaseValues[nameof(Document.DocumentDate)];
            if (dbDate is not null)
                doc.DocumentDate = dbDate;

            entry.OriginalValues.SetValues(databaseValues);
            await db.SaveChangesAsync();
        }
    }
}
