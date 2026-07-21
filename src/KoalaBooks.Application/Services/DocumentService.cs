using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.IO.Compression;

namespace KoalaBooks.Application.Services;

public class DocumentService(
    AppDbContext db,
    IDocumentStorage storage,
    IDocumentExtractionQueue extractionQueue,
    IZipImportQueue zipImportQueue,
    IBackgroundJobRunService backgroundJobRunService,
    ICurrentUser currentUser) : IDocumentService
{
    private const string NotFoundMessage = "Dokumentet hittades inte.";

    private const long MaxBytes = 10 * 1024 * 1024;
    private const long ZipMaxBytes = 500 * 1024 * 1024;
    private const int ZipMaxEntries = 500;

    private static readonly HashSet<string> AllowedContentTypes =
    [
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/jpg", // Some browsers report .jpg files as image/jpg rather than image/jpeg
    ];

    private sealed class DocumentTooLargeException : Exception;

    private sealed class MaxBytesEnforcingStream(Stream inner, long maxBytes) : Stream
    {
        private long _totalRead;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _totalRead += read;
            if (_totalRead > maxBytes) throw new DocumentTooLargeException();
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<(Document? Doc, string? Error)> UploadAsync(string fileName, string contentType, Func<Stream> openData)
    {
        if (currentUser.OrganisationId is null)
            return (null, "Ingen aktiv organisation.");
        if (!AllowedContentTypes.Contains(contentType))
            return (null, "Otillåten filtyp. Tillåtna typer: PDF, PNG, JPEG.");

        var doc = new Document
        {
            OrganisationId = currentUser.OrganisationId.Value,
            FileName = fileName,
            ContentType = contentType,
            FileSize = 0,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync().ConfigureAwait(false); // gets doc.Id

        try
        {
            (doc.StorageKey, doc.FileSize) = await storage.SaveAsync(
                doc.Id, contentType, () => new MaxBytesEnforcingStream(openData(), MaxBytes)).ConfigureAwait(false);
        }
        catch (DocumentTooLargeException)
        {
            db.Documents.Remove(doc);
            await db.SaveChangesAsync().ConfigureAwait(false);
            return (null, "Filen är för stor (max 10 MB).");
        }
        catch (Exception ex)
        {
            // Storage failed — roll back the DB row to avoid orphaned metadata
            db.Documents.Remove(doc);
            await db.SaveChangesAsync().ConfigureAwait(false);
            return (null, $"Lagring misslyckades: {ex.Message}");
        }

        doc.ExtractionStatus = ExtractionStatus.Pending;
        await db.SaveChangesAsync().ConfigureAwait(false);
        extractionQueue.Enqueue(doc.Id);

        return (doc, null);
    }

    public virtual async Task<(bool Found, string? Error)> UpdateMetadataAsync(int documentId, string? classifiedType, DateOnly? documentDate)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId).ConfigureAwait(false);
        if (doc is null) return (false, NotFoundMessage);
        doc.ClassifiedType = classifiedType;
        doc.DocumentDate = documentDate;
        return await SaveChangesResolvingConcurrencyAsync().ConfigureAwait(false);
    }

    // Scoped AppDbContext lives for the whole Blazor circuit, so a Document tracked earlier
    // (e.g. by UploadAsync) can go stale once the background extraction job updates it.
    // Found is false only when the row disappeared entirely (not merely stale) — callers
    // that need to distinguish "not found" from "found but failed to save" should check it
    // instead of pattern-matching Error's text.
    private async Task<(bool Found, string? Error)> SaveChangesResolvingConcurrencyAsync()
    {
        try
        {
            await db.SaveChangesAsync().ConfigureAwait(false);
            return (true, null);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var entry = ex.Entries.Single();
            var databaseValues = await entry.GetDatabaseValuesAsync().ConfigureAwait(false);
            if (databaseValues is null) return (false, NotFoundMessage);

            // Refresh only the concurrency token, not the whole entity — this method never
            // touches SuggestedType/ExtractionStatus, so don't let their stale tracked values overwrite the DB.
            entry.Property("xmin").OriginalValue = databaseValues["xmin"];

            try
            {
                await db.SaveChangesAsync().ConfigureAwait(false);
                return (true, null);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A second collision on the same save is rare enough not to warrant looping —
                // surface it and let the user retry instead of crashing the circuit.
                return (true, "Kunde inte spara just nu. Försök igen.");
            }
        }
    }

    public Task<List<DocumentMeta>> GetPendingAsync(
        string? typeFilter = null,
        int skip = 0,
        int? take = null,
        string sortBy = "uploadedAt",
        bool sortAsc = false,
        DateOnly? fiscalYearStart = null,
        DateOnly? fiscalYearEnd = null,
        bool undatedOnly = false)
    {
        var base2 = PendingQuery(typeFilter, fiscalYearStart, fiscalYearEnd, undatedOnly);
        IQueryable<Document> ordered = (sortBy, sortAsc) switch
        {
            ("fileName",     true)  => base2.OrderBy(d => d.FileName),
            ("fileName",     false) => base2.OrderByDescending(d => d.FileName),
            ("documentDate", true)  => base2.OrderBy(d => d.DocumentDate),
            ("documentDate", false) => base2.OrderByDescending(d => d.DocumentDate),
            (_,              true)  => base2.OrderBy(d => d.UploadedAt),
            _                       => base2.OrderByDescending(d => d.UploadedAt),
        };
        var q = ordered.Skip(skip);
        if (take.HasValue) q = q.Take(take.Value);
        return SelectMetaAsync(q);
    }

    public Task<int> GetPendingCountAsync(
        string? typeFilter = null,
        DateOnly? fiscalYearStart = null,
        DateOnly? fiscalYearEnd = null,
        bool undatedOnly = false) =>
        PendingQuery(typeFilter, fiscalYearStart, fiscalYearEnd, undatedOnly).CountAsync();

    private IQueryable<Document> PendingQuery(
        string? typeFilter,
        DateOnly? fiscalYearStart = null,
        DateOnly? fiscalYearEnd = null,
        bool undatedOnly = false)
    {
        var query = db.Documents
            .Where(d => !d.JournalEntries.Any() && !d.SupplierInvoices.Any() && !d.CustomerInvoices.Any());

        query = typeFilter switch
        {
            "unclassified" => query.Where(d => d.ClassifiedType == null),
            null or "all"  => query,
            var t          => query.Where(d => d.ClassifiedType == t)
        };

        if (undatedOnly)
            return query.Where(d => d.DocumentDate == null);

        if (fiscalYearStart.HasValue && fiscalYearEnd.HasValue)
            return query.Where(d => d.DocumentDate >= fiscalYearStart && d.DocumentDate <= fiscalYearEnd);

        return query;
    }

    public Task<List<DocumentMeta>> GetLinkedAsync(DocumentEntityType entityType, int entityId)
    {
        var query = entityType switch
        {
            DocumentEntityType.JournalEntry    => db.Documents.Where(d => d.JournalEntries.Any(j => j.Id == entityId)),
            DocumentEntityType.SupplierInvoice => db.Documents.Where(d => d.SupplierInvoices.Any(s => s.Id == entityId)),
            DocumentEntityType.CustomerInvoice => db.Documents.Where(d => d.CustomerInvoices.Any(c => c.Id == entityId)),
            _                                  => db.Documents.Where(_ => false)
        };
        return SelectMetaAsync(query);
    }

    public async Task<Dictionary<int, int>> GetCountsForJournalEntriesAsync(IEnumerable<int> entryIds)
    {
        var ids = entryIds.ToHashSet();
        return await db.JournalEntries
            .Where(j => ids.Contains(j.Id))
            .Select(j => new { j.Id, Count = j.Documents.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count).ConfigureAwait(false);
    }

    public async Task<(string ContentType, byte[] Data, string FileName)?> GetDownloadAsync(int documentId)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId).ConfigureAwait(false);
        if (doc is null) return null;
        var data = await storage.LoadAsync(doc.StorageKey).ConfigureAwait(false);
        return (doc.ContentType, data, doc.FileName);
    }

    public async Task<bool> DeleteAsync(int documentId)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId).ConfigureAwait(false);
        if (doc is null) return false;
        await storage.DeleteAsync(doc.StorageKey).ConfigureAwait(false);
        db.Documents.Remove(doc);
        return await DeleteResolvingConcurrencyAsync().ConfigureAwait(false);
    }

    // Same staleness risk as SaveChangesResolvingConcurrencyAsync: doc may have been
    // tracked earlier in this circuit and gone stale since. Unlike an update, a mismatch
    // here just means someone else already changed or deleted the row — either way the
    // delete's goal (row gone) is met, so re-fetching the current xmin and retrying once
    // is enough; a missing row on retry means it's already gone.
    private async Task<bool> DeleteResolvingConcurrencyAsync()
    {
        try
        {
            await db.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var entry = ex.Entries.Single();
            var databaseValues = await entry.GetDatabaseValuesAsync().ConfigureAwait(false);
            if (databaseValues is null) return true;

            entry.Property("xmin").OriginalValue = databaseValues["xmin"];
            await db.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }
    }

    // Resolves the document and the target entity in one pass so callers (the REST
    // controller) don't need a separate existence pre-check — that would both cost an
    // extra query and leave a TOCTOU gap between the check and this method's own lookup.
    public async Task<LinkOutcome> LinkAsync(int documentId, DocumentEntityType entityType, int entityId)
    {
        var doc = await db.Documents
            .Include(d => d.JournalEntries)
            .Include(d => d.SupplierInvoices)
            .Include(d => d.CustomerInvoices)
            .FirstOrDefaultAsync(d => d.Id == documentId).ConfigureAwait(false);
        if (doc is null) return LinkOutcome.DocumentNotFound;

        bool entityFound;
        switch (entityType)
        {
            case DocumentEntityType.JournalEntry:
                var entry = await db.JournalEntries.FindAsync(entityId).ConfigureAwait(false);
                entityFound = entry is not null;
                if (entityFound && !doc.JournalEntries.Any(j => j.Id == entityId))
                    doc.JournalEntries.Add(entry!);
                break;
            case DocumentEntityType.SupplierInvoice:
                var inv = await db.SupplierInvoices.FindAsync(entityId).ConfigureAwait(false);
                entityFound = inv is not null;
                if (entityFound && !doc.SupplierInvoices.Any(s => s.Id == entityId))
                    doc.SupplierInvoices.Add(inv!);
                break;
            case DocumentEntityType.CustomerInvoice:
                var cinv = await db.CustomerInvoices.FindAsync(entityId).ConfigureAwait(false);
                entityFound = cinv is not null;
                if (entityFound && !doc.CustomerInvoices.Any(c => c.Id == entityId))
                    doc.CustomerInvoices.Add(cinv!);
                break;
            default:
                entityFound = false;
                break;
        }
        if (!entityFound) return LinkOutcome.EntityNotFound;

        var saved = await SaveChangesRetryingConcurrencyAsync().ConfigureAwait(false);
        return saved ? LinkOutcome.Linked : LinkOutcome.ConcurrencyConflict;
    }

    // Same staleness risk as SaveChangesResolvingConcurrencyAsync, but a Link save can
    // have multiple stale entries at once (the Document and/or the newly-fetched target
    // entity), so this refreshes every conflicting entry's xmin rather than assuming one.
    private async Task<bool> SaveChangesRetryingConcurrencyAsync()
    {
        try
        {
            await db.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                var databaseValues = await entry.GetDatabaseValuesAsync().ConfigureAwait(false);
                if (databaseValues is null) continue;
                entry.Property("xmin").OriginalValue = databaseValues["xmin"];
            }

            try
            {
                await db.SaveChangesAsync().ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }
    }

    public async Task<(Document? Doc, string? Error)> UploadAndLinkAsync(
        string fileName, string contentType, Func<Stream> openData, DocumentEntityType entityType, int entityId)
    {
        var (doc, err) = await UploadAsync(fileName, contentType, openData).ConfigureAwait(false);
        if (doc is null) return (null, err);
        await LinkAsync(doc.Id, entityType, entityId).ConfigureAwait(false);
        return (doc, null);
    }

    public async Task<(int? RunId, string? Error)> UploadZipAsync(string fileName, Func<Stream> openZipData)
    {
        if (currentUser.OrganisationId is null)
            return (null, "Ingen aktiv organisation.");

        var tempPath = Path.GetTempFileName();
        try
        {
            long totalBytes;
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
            await using (var tempWriteStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            await using (var source = openZipData())
#pragma warning restore CA2007
            {
                var buffer = new byte[81920];
                totalBytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > ZipMaxBytes)
                    {
                        return (null, "Zip-filen är för stor (max 500 MB).");
                    }
                    await tempWriteStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                }
            }

            int entryCount;
            try
            {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tempReadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
#pragma warning restore CA2007
                using var archive = new ZipArchive(tempReadStream, ZipArchiveMode.Read);
                entryCount = archive.Entries.Count(e => !string.IsNullOrEmpty(e.Name));
            }
            catch (InvalidDataException)
            {
                return (null, "Ogiltig zip-fil.");
            }

            if (entryCount > ZipMaxEntries)
                return (null, $"För många filer i zip-filen (max {ZipMaxEntries}).");

            var strategy = db.Database.CreateExecutionStrategy();
            var (runId, stagingOid) = await strategy.ExecuteAsync(async () =>
            {
                // A retry re-runs this whole delegate: a prior failed attempt may have
                // left a BackgroundJobRun row tracked (Added) without committing —
                // detach it before adding a fresh one, or SaveChangesAsync would insert
                // both and produce a duplicate row.
                foreach (var stale in db.ChangeTracker.Entries<BackgroundJobRun>().Where(e => e.State == EntityState.Added).ToList())
                    stale.State = EntityState.Detached;

#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tempReadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
#pragma warning restore CA2007
                var (oid, _) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, tempReadStream).ConfigureAwait(false);

                var run = await backgroundJobRunService.CreateRunAsync(BackgroundJobType.ZipImport, entryCount).ConfigureAwait(false);

                await tx.CommitAsync().ConfigureAwait(false);
                return (run.Id, oid);
            }).ConfigureAwait(false);

            zipImportQueue.Enqueue(runId, fileName, stagingOid);

            return (runId, null);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private async Task<List<DocumentMeta>> SelectMetaAsync(IQueryable<Document> query)
    {
        var rows = await query.Select(d => new
        {
            Meta = new DocumentMeta
            {
                Id = d.Id,
                FileName = d.FileName,
                ContentType = d.ContentType,
                FileSize = d.FileSize,
                UploadedAt = d.UploadedAt,
                ClassifiedType = d.ClassifiedType,
                SuggestedType = d.SuggestedType,
                ExtractedDataJson = d.ExtractedDataJson,
                DocumentDate = d.DocumentDate,
                ExtractionStatus = d.ExtractionStatus
            },
            Xmin = EF.Property<uint>(d, "xmin")
        }).ToListAsync().ConfigureAwait(false);

        // Piggyback on this read to refresh the xmin of any Document already tracked in this
        // circuit (e.g. from UploadAsync), so polling keeps stale entities from ever forming.
        var trackedById = db.ChangeTracker.Entries<Document>().ToDictionary(e => e.Entity.Id);
        foreach (var row in rows)
        {
            if (trackedById.TryGetValue(row.Meta.Id, out var entry))
                entry.Property("xmin").OriginalValue = row.Xmin;
        }

        return rows.Select(r => r.Meta).ToList();
    }
}
