using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Text.Json;

namespace KoalaBooks.Application.Services;

public class DocumentService(
    AppDbContext db,
    IDocumentStorage storage,
    IDocumentExtractor extractor,
    ICurrentUser currentUser,
    ILogger<DocumentService> logger)
{
    private const long MaxBytes = 10 * 1024 * 1024;

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

    public async Task<(Document? Doc, string? Error)> UploadAsync(string fileName, string contentType, byte[] data)
    {
        if (data.Length > MaxBytes)
            return (null, "Filen är för stor (max 10 MB).");
        if (currentUser.OrganisationId is null)
            return (null, "Ingen aktiv organisation.");
        if (!AllowedContentTypes.Contains(contentType))
            return (null, "Otillåten filtyp. Tillåtna typer: PDF, PNG, JPEG.");

        var doc = new Document
        {
            OrganisationId = currentUser.OrganisationId.Value,
            FileName = fileName,
            ContentType = contentType,
            FileSize = data.Length,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync(); // gets doc.Id

        try
        {
            doc.StorageKey = await storage.SaveAsync(doc.Id, contentType, data);
        }
        catch (Exception ex)
        {
            // Storage failed — roll back the DB row to avoid orphaned metadata
            db.Documents.Remove(doc);
            await db.SaveChangesAsync();
            return (null, $"Lagring misslyckades: {ex.Message}");
        }

        try
        {
            var result = await extractor.ExtractAsync(fileName, contentType, data);
            doc.SuggestedType = result.SuggestedType;
            doc.ExtractedDataJson = result.SuggestedType is not null
                ? JsonSerializer.Serialize(result)
                : null;
            doc.DocumentDate = result.InvoiceDate;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Extraction failed for {FileName} — upload proceeds without suggestion", fileName);
        }

        await db.SaveChangesAsync();
        return (doc, null);
    }

    public async Task<string?> UpdateMetadataAsync(int documentId, string? classifiedType, DateOnly? documentDate)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return "Dokumentet hittades inte.";
        doc.ClassifiedType = classifiedType;
        doc.DocumentDate = documentDate;
        await db.SaveChangesAsync();
        return null;
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
        await db.SaveChangesAsync();
        return true;
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
        string fileName, string contentType, byte[] data, DocumentEntityType entityType, int entityId)
    {
        var (doc, err) = await UploadAsync(fileName, contentType, data);
        if (doc is null) return (null, err);
        await LinkAsync(doc.Id, entityType, entityId);
        return (doc, null);
    }

    public async Task<(ZipImportResult? Result, string? Error)> UploadZipAsync(byte[] zipData)
    {
        using var ms = new MemoryStream(zipData);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        var imported = new List<Document>();
        var skipped = new List<(string FileName, string Reason)>();

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue; // directory entry — ZipArchiveEntry.Name is empty for these

            if (!ZipEntryContentTypes.TryGetValue(Path.GetExtension(entry.Name), out var contentType))
            {
                skipped.Add((entry.Name, "Otillåten filtyp."));
                continue;
            }

            if (entry.Length > MaxBytes)
            {
                skipped.Add((entry.Name, "Filen är för stor (max 10 MB)."));
                continue;
            }

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            await entryStream.CopyToAsync(buffer);

            var (doc, err) = await UploadAsync(entry.Name, contentType, buffer.ToArray());
            if (doc is not null)
                imported.Add(doc);
            else
                skipped.Add((entry.Name, err ?? "Okänt fel."));
        }

        return (new ZipImportResult(imported, skipped), null);
    }

    private static Task<List<DocumentMeta>> SelectMetaAsync(IQueryable<Document> query) =>
        query.Select(d => new DocumentMeta
        {
            Id = d.Id,
            FileName = d.FileName,
            ContentType = d.ContentType,
            FileSize = d.FileSize,
            UploadedAt = d.UploadedAt,
            ClassifiedType = d.ClassifiedType,
            SuggestedType = d.SuggestedType,
            ExtractedDataJson = d.ExtractedDataJson,
            DocumentDate = d.DocumentDate
        }).ToListAsync();
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

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:N1} KB",
        _ => $"{FileSize / (1024.0 * 1024):N1} MB"
    };
}

public record ZipImportResult(IReadOnlyList<Document> Imported, IReadOnlyList<(string FileName, string Reason)> Skipped);
