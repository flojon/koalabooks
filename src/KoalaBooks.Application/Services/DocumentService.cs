using KoalaBooks.Application.Jobs;
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
    ICurrentUser currentUser)
{
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

    private static readonly Dictionary<string, string> ZipEntryContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };

    private sealed class DocumentTooLargeException : Exception;

    private sealed class MaxBytesEnforcingStream(Stream inner, long maxBytes) : Stream
    {
        private long _totalRead;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
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
            await inner.DisposeAsync();
            await base.DisposeAsync();
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
        await db.SaveChangesAsync(); // gets doc.Id

        try
        {
            (doc.StorageKey, doc.FileSize) = await storage.SaveAsync(
                doc.Id, contentType, () => new MaxBytesEnforcingStream(openData(), MaxBytes));
        }
        catch (DocumentTooLargeException)
        {
            db.Documents.Remove(doc);
            await db.SaveChangesAsync();
            return (null, "Filen är för stor (max 10 MB).");
        }
        catch (Exception ex)
        {
            // Storage failed — roll back the DB row to avoid orphaned metadata
            db.Documents.Remove(doc);
            await db.SaveChangesAsync();
            return (null, $"Lagring misslyckades: {ex.Message}");
        }

        doc.ExtractionStatus = ExtractionStatus.Pending;
        await db.SaveChangesAsync();
        extractionQueue.Enqueue(doc.Id);

        return (doc, null);
    }

    public virtual async Task<string?> UpdateMetadataAsync(int documentId, string? classifiedType, DateOnly? documentDate)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return "Dokumentet hittades inte.";
        doc.ClassifiedType = classifiedType;
        doc.DocumentDate = documentDate;
        return await SaveChangesResolvingConcurrencyAsync(doc);
    }

    // Scoped AppDbContext lives for the whole Blazor circuit, so a Document tracked earlier
    // (e.g. by UploadAsync) can go stale once the background extraction job updates it.
    private async Task<string?> SaveChangesResolvingConcurrencyAsync(Document doc)
    {
        try
        {
            await db.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var entry = ex.Entries.Single();
            var databaseValues = await entry.GetDatabaseValuesAsync();
            if (databaseValues is null) return "Dokumentet hittades inte.";

            // Refresh only the concurrency token, not the whole entity — this method never
            // touches SuggestedType/ExtractionStatus, so don't let their stale tracked values overwrite the DB.
            entry.Property("xmin").OriginalValue = databaseValues["xmin"];

            try
            {
                await db.SaveChangesAsync();
                return null;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A second collision on the same save is rare enough not to warrant looping —
                // surface it and let the user retry instead of crashing the circuit.
                return "Kunde inte spara just nu. Försök igen.";
            }
        }
    }

    public Task<List<DocumentMeta>> GetPendingAsync(
        string? typeFilter = null,
        int skip = 0,
        int? take = null,
        string sortBy = "uploadedAt",
        bool sortAsc = false)
    {
        var base2 = PendingQuery(typeFilter);
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

    public Task<int> GetPendingCountAsync(string? typeFilter = null) =>
        PendingQuery(typeFilter).CountAsync();

    public async Task<List<ZipBatchStatus>> GetOpenZipBatchesAsync() =>
        await db.ZipImportBatches
            .Where(b => !b.Acknowledged)
            .Select(b => new ZipBatchStatus
            {
                Id = b.Id,
                TotalEntries = b.TotalEntries,
                ProcessedEntries = b.ProcessedEntries,
                ImportedCount = b.ImportedCount,
                SkippedCount = b.SkippedCount,
                SkippedReasonsJson = b.SkippedReasonsJson,
                Done = b.Done,
                CreatedAt = b.CreatedAt,
            })
            .ToListAsync();

    public async Task AcknowledgeZipBatchAsync(int batchId)
    {
        var batch = await db.ZipImportBatches.FirstOrDefaultAsync(b => b.Id == batchId);
        if (batch is null) return;
        batch.Acknowledged = true;
        await db.SaveChangesAsync();
    }

    private IQueryable<Document> PendingQuery(string? typeFilter)
    {
        var query = db.Documents
            .Where(d => !d.JournalEntries.Any() && !d.SupplierInvoices.Any() && !d.CustomerInvoices.Any());

        return typeFilter switch
        {
            "unclassified" => query.Where(d => d.ClassifiedType == null),
            null or "all"  => query,
            var t          => query.Where(d => d.ClassifiedType == t)
        };
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
            .ToDictionaryAsync(x => x.Id, x => x.Count);
    }

    public async Task<(string ContentType, byte[] Data, string FileName)?> GetDownloadAsync(int documentId)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return null;
        var data = await storage.LoadAsync(doc.StorageKey);
        return (doc.ContentType, data, doc.FileName);
    }

    public async Task<bool> DeleteAsync(int documentId)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return false;
        await storage.DeleteAsync(doc.StorageKey);
        db.Documents.Remove(doc);
        return await DeleteResolvingConcurrencyAsync(doc);
    }

    // Same staleness risk as SaveChangesResolvingConcurrencyAsync: doc may have been
    // tracked earlier in this circuit and gone stale since. Unlike an update, a mismatch
    // here just means someone else already changed or deleted the row — either way the
    // delete's goal (row gone) is met, so re-fetching the current xmin and retrying once
    // is enough; a missing row on retry means it's already gone.
    private async Task<bool> DeleteResolvingConcurrencyAsync(Document doc)
    {
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var entry = ex.Entries.Single();
            var databaseValues = await entry.GetDatabaseValuesAsync();
            if (databaseValues is null) return true;

            entry.Property("xmin").OriginalValue = databaseValues["xmin"];
            await db.SaveChangesAsync();
            return true;
        }
    }

    public async Task LinkAsync(int documentId, DocumentEntityType entityType, int entityId)
    {
        var doc = await db.Documents
            .Include(d => d.JournalEntries)
            .Include(d => d.SupplierInvoices)
            .Include(d => d.CustomerInvoices)
            .FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return;

        switch (entityType)
        {
            case DocumentEntityType.JournalEntry:
                var entry = await db.JournalEntries.FindAsync(entityId);
                if (entry is not null && !doc.JournalEntries.Any(j => j.Id == entityId))
                    doc.JournalEntries.Add(entry);
                break;
            case DocumentEntityType.SupplierInvoice:
                var inv = await db.SupplierInvoices.FindAsync(entityId);
                if (inv is not null && !doc.SupplierInvoices.Any(s => s.Id == entityId))
                    doc.SupplierInvoices.Add(inv);
                break;
            case DocumentEntityType.CustomerInvoice:
                var cinv = await db.CustomerInvoices.FindAsync(entityId);
                if (cinv is not null && !doc.CustomerInvoices.Any(c => c.Id == entityId))
                    doc.CustomerInvoices.Add(cinv);
                break;
        }
        await db.SaveChangesAsync();
    }

    public async Task<(Document? Doc, string? Error)> UploadAndLinkAsync(
        string fileName, string contentType, Func<Stream> openData, DocumentEntityType entityType, int entityId)
    {
        var (doc, err) = await UploadAsync(fileName, contentType, openData);
        if (doc is null) return (null, err);
        await LinkAsync(doc.Id, entityType, entityId);
        return (doc, null);
    }

    public async Task<(int? BatchId, string? Error)> UploadZipAsync(Func<Stream> openZipData)
    {
        if (currentUser.OrganisationId is null)
            return (null, "Ingen aktiv organisation.");

        var tempPath = Path.GetTempFileName();
        try
        {
            long totalBytes;
            await using (var tempWriteStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            await using (var source = openZipData())
            {
                var buffer = new byte[81920];
                totalBytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > ZipMaxBytes)
                    {
                        return (null, "Zip-filen är för stor (max 500 MB).");
                    }
                    await tempWriteStream.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            int entryCount;
            try
            {
                await using var tempReadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
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
            var batchId = await strategy.ExecuteAsync(async () =>
            {
                // A retry re-runs this whole delegate: a prior failed attempt may
                // have left a ZipImportBatch row tracked (Added) without committing
                // — detach it before adding a fresh one, or SaveChangesAsync would
                // insert both and produce a duplicate row.
                foreach (var stale in db.ChangeTracker.Entries<ZipImportBatch>().Where(e => e.State == EntityState.Added).ToList())
                    stale.State = EntityState.Detached;

                await using var tx = await db.Database.BeginTransactionAsync();
                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
                await using var tempReadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                var (oid, _) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, tempReadStream);

                var batch = new ZipImportBatch
                {
                    OrganisationId = currentUser.OrganisationId.Value,
                    StagingOid = oid,
                    TotalEntries = entryCount,
                };
                db.ZipImportBatches.Add(batch);
                await db.SaveChangesAsync();

                await tx.CommitAsync();
                return batch.Id;
            });

            zipImportQueue.Enqueue(batchId);

            return (batchId, null);
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
        }).ToListAsync();

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

public class DocumentMeta
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? ClassifiedType { get; set; }
    public string? SuggestedType { get; set; }
    public string? ExtractedDataJson { get; set; }
    public DateOnly? DocumentDate { get; set; }
    public ExtractionStatus ExtractionStatus { get; set; }

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:N1} KB",
        _ => $"{FileSize / (1024.0 * 1024):N1} MB"
    };

    /// <summary>
    /// Resolves the date to pre-fill in a document's date field: the persisted
    /// (possibly user-edited) document date takes precedence over the AI-extracted
    /// invoice date, since it reflects the value the user last confirmed.
    /// </summary>
    public static DateTime? ResolvePrefillDate(DateOnly? documentDate, DateOnly? extractedInvoiceDate) =>
        (documentDate ?? extractedInvoiceDate)?.ToDateTime(TimeOnly.MinValue);
}

public record ZipImportResult(IReadOnlyList<Document> Imported, IReadOnlyList<(string FileName, string Reason)> Skipped);

public class ZipBatchStatus
{
    public int Id { get; set; }
    public int TotalEntries { get; set; }
    public int ProcessedEntries { get; set; }
    public int ImportedCount { get; set; }
    public int SkippedCount { get; set; }
    public string SkippedReasonsJson { get; set; } = "[]";
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<SkippedEntry> SkippedReasons =>
        System.Text.Json.JsonSerializer.Deserialize<List<SkippedEntry>>(SkippedReasonsJson) ?? [];
}
