using Hangfire;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace KoalaBooks.Application.Jobs;

public class DocumentExtractionJob(
    DbContextOptions<AppDbContext> dbOptions,
    IDocumentExtractor extractor,
    ILogger<DocumentExtractionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(int documentId)
    {
        // This job has no HttpContext, so a DI-resolved ICurrentUser/AppDbContext would
        // always see OrganisationId == null — which, since AppDbContext's DocumentData
        // query filter follows Document.OrganisationId, would make DbDocumentStorage's
        // FindAsync-based lookups silently return "not found" for every document. Instead,
        // build our own AppDbContext (and DbDocumentStorage on top of it) bound to a mutable
        // LocalCurrentUser, the same way ZipImportJob does — starting with no org (so the
        // initial lookup, done with IgnoreQueryFilters, is unaffected either way) and then
        // setting it to the document's own OrganisationId once known, so the rest of this
        // context's queries — including storage's — scope correctly from that point on.
        var tenant = new LocalCurrentUser();
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
        await using var db = new AppDbContext(dbOptions, tenant);
#pragma warning restore CA2007
        var storage = new DbDocumentStorage(db);

        // IgnoreQueryFilters: tenant is still unset at this point (see above). Safe here
        // because the job only ever acts on a documentId handed to it by trusted code that
        // just created that exact row — not arbitrary tenant-crossing input.
        var doc = await db.Documents.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == documentId).ConfigureAwait(false);
        if (doc is null) return;
        tenant.OrganisationId = doc.OrganisationId;

        var data = await storage.LoadAsync(doc.StorageKey).ConfigureAwait(false); // storage/DB failures bubble → Hangfire retries (Attempts = 3)

        try
        {
            var result = await extractor.ExtractAsync(doc.FileName, doc.ContentType, data).ConfigureAwait(false);
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

        await SaveChangesResolvingConcurrencyAsync(db, doc).ConfigureAwait(false);
    }

    // Document.Xmin (Postgres' native row-version column) is a concurrency token, so a
    // write that landed on this row between our read above and this save — most notably
    // the user classifying the document via "Bokför" while extraction was in flight —
    // raises DbUpdateConcurrencyException instead of being silently overwritten. Resolve
    // it by deferring to whatever DocumentDate is in the database now (if any), then retry
    // once against the current row version.
    private static async Task SaveChangesResolvingConcurrencyAsync(AppDbContext db, Document doc)
    {
        try
        {
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // ex.Entries yields the non-generic EntityEntry wrapper even though the
            // tracked instance is a Document — EntityEntry<T> is never what's reported here.
            var entry = ex.Entries.Single();
            var databaseValues = await entry.GetDatabaseValuesAsync().ConfigureAwait(false);
            if (databaseValues is null)
            {
                // The document was deleted concurrently — nothing left to update.
                return;
            }

            var dbDate = (DateOnly?)databaseValues[nameof(Document.DocumentDate)];
            if (dbDate is not null)
                doc.DocumentDate = dbDate;

            entry.OriginalValues.SetValues(databaseValues);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }
}
