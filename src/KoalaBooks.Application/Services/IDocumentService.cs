using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Application.Services;

public interface IDocumentService
{
    Task<(Document? Doc, string? Error)> UploadAsync(string fileName, string contentType, Func<Stream> openData);
    Task<string?> UpdateMetadataAsync(int documentId, string? classifiedType, DateOnly? documentDate);
    Task<List<DocumentMeta>> GetPendingAsync(
        string? typeFilter = null,
        int skip = 0,
        int? take = null,
        string sortBy = "uploadedAt",
        bool sortAsc = false);
    Task<int> GetPendingCountAsync(string? typeFilter = null);
    Task<List<DocumentMeta>> GetLinkedAsync(DocumentEntityType entityType, int entityId);
    Task<Dictionary<int, int>> GetCountsForJournalEntriesAsync(IEnumerable<int> entryIds);
    Task<(string ContentType, byte[] Data, string FileName)?> GetDownloadAsync(int documentId);
    Task<bool> DeleteAsync(int documentId);
    Task LinkAsync(int documentId, DocumentEntityType entityType, int entityId);
    Task<(Document? Doc, string? Error)> UploadAndLinkAsync(
        string fileName, string contentType, Func<Stream> openData, DocumentEntityType entityType, int entityId);
    Task<(int? BatchId, string? Error)> UploadZipAsync(Func<Stream> openZipData);
    Task<List<ZipBatchStatus>> GetOpenZipBatchesAsync();
    Task AcknowledgeZipBatchAsync(int batchId);
}
