using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Interfaces;

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

public interface IDocumentService
{
    Task<(Document? Doc, string? Error)> UploadAsync(string fileName, string contentType, Func<Stream> openData);
    Task<(bool Found, string? Error)> UpdateMetadataAsync(int documentId, string? classifiedType, DateOnly? documentDate);
    Task<List<DocumentMeta>> GetPendingAsync(
        string? typeFilter = null,
        int skip = 0,
        int? take = null,
        string sortBy = "uploadedAt",
        bool sortAsc = false,
        DateOnly? fiscalYearStart = null,
        DateOnly? fiscalYearEnd = null,
        bool undatedOnly = false);
    Task<int> GetPendingCountAsync(
        string? typeFilter = null,
        DateOnly? fiscalYearStart = null,
        DateOnly? fiscalYearEnd = null,
        bool undatedOnly = false);
    Task<List<DocumentMeta>> GetLinkedAsync(DocumentEntityType entityType, int entityId);
    Task<Dictionary<int, int>> GetCountsForJournalEntriesAsync(IEnumerable<int> entryIds);
    Task<(string ContentType, byte[] Data, string FileName)?> GetDownloadAsync(int documentId);
    Task<bool> DeleteAsync(int documentId);
    Task<LinkOutcome> LinkAsync(int documentId, DocumentEntityType entityType, int entityId);
    Task<(Document? Doc, string? Error)> UploadAndLinkAsync(
        string fileName, string contentType, Func<Stream> openData, DocumentEntityType entityType, int entityId);
    Task<(int? RunId, string? Error)> UploadZipAsync(string fileName, Func<Stream> openZipData);
}
