// src/KoalaBooks.Application/Services/DocumentService.cs
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace KoalaBooks.Application.Services;

public class DocumentService(AppDbContext db, IDocumentStorage storage, IDocumentExtractor extractor, ICurrentUser currentUser)
{
    private const long MaxBytes = 10 * 1024 * 1024;

    public async Task<(Document? Doc, string? Error)> UploadAsync(string fileName, string contentType, byte[] data)
    {
        if (data.Length > MaxBytes)
            return (null, "Filen är för stor (max 10 MB).");
        if (currentUser.OrganisationId is null)
            return (null, "Ingen aktiv organisation.");

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
            doc.ClassifiedType = result.SuggestedType;
            doc.ExtractedDataJson = result.SuggestedType is not null
                ? JsonSerializer.Serialize(result)
                : null;
        }
        catch { /* extraction failure must not block upload */ }

        await db.SaveChangesAsync();
        return (doc, null);
    }

    public async Task<string?> SetTypeAsync(int documentId, string? classifiedType)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return "Dokumentet hittades inte.";
        doc.ClassifiedType = classifiedType;
        await db.SaveChangesAsync();
        return null;
    }

    public async Task<List<DocumentMeta>> GetPendingAsync() =>
        await db.Documents
            .Where(d => !d.JournalEntries.Any() && !d.SupplierInvoices.Any() && !d.CustomerInvoices.Any())
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => new DocumentMeta
            {
                Id = d.Id,
                FileName = d.FileName,
                ContentType = d.ContentType,
                FileSize = d.FileSize,
                UploadedAt = d.UploadedAt,
                ClassifiedType = d.ClassifiedType,
                SuggestedType = d.SuggestedType,
                ExtractedDataJson = d.ExtractedDataJson
            })
            .ToListAsync();

    public async Task<List<DocumentMeta>> GetLinkedAsync(DocumentEntityType entityType, int entityId) =>
        entityType switch
        {
            DocumentEntityType.JournalEntry =>
                await db.Documents.Where(d => d.JournalEntries.Any(j => j.Id == entityId))
                    .Select(d => new DocumentMeta
                    {
                        Id = d.Id,
                        FileName = d.FileName,
                        ContentType = d.ContentType,
                        FileSize = d.FileSize,
                        UploadedAt = d.UploadedAt,
                        ClassifiedType = d.ClassifiedType,
                        SuggestedType = d.SuggestedType,
                        ExtractedDataJson = d.ExtractedDataJson
                    }).ToListAsync(),
            DocumentEntityType.SupplierInvoice =>
                await db.Documents.Where(d => d.SupplierInvoices.Any(s => s.Id == entityId))
                    .Select(d => new DocumentMeta
                    {
                        Id = d.Id,
                        FileName = d.FileName,
                        ContentType = d.ContentType,
                        FileSize = d.FileSize,
                        UploadedAt = d.UploadedAt,
                        ClassifiedType = d.ClassifiedType,
                        SuggestedType = d.SuggestedType,
                        ExtractedDataJson = d.ExtractedDataJson
                    }).ToListAsync(),
            DocumentEntityType.CustomerInvoice =>
                await db.Documents.Where(d => d.CustomerInvoices.Any(c => c.Id == entityId))
                    .Select(d => new DocumentMeta
                    {
                        Id = d.Id,
                        FileName = d.FileName,
                        ContentType = d.ContentType,
                        FileSize = d.FileSize,
                        UploadedAt = d.UploadedAt,
                        ClassifiedType = d.ClassifiedType,
                        SuggestedType = d.SuggestedType,
                        ExtractedDataJson = d.ExtractedDataJson
                    }).ToListAsync(),
            _ => []
        };

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

    private static DocumentMeta ToMeta(Document d) => new()
    {
        Id = d.Id,
        FileName = d.FileName,
        ContentType = d.ContentType,
        FileSize = d.FileSize,
        UploadedAt = d.UploadedAt,
        ClassifiedType = d.ClassifiedType,
        SuggestedType = d.SuggestedType,
        ExtractedDataJson = d.ExtractedDataJson
    };
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

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:N1} KB",
        _ => $"{FileSize / (1024.0 * 1024):N1} MB"
    };
}
