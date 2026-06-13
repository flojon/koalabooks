namespace KoalaBooks.Domain.Entities;

public class Document
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string StorageKey { get; set; } = "";

    public string? SuggestedType { get; set; }
    public string? ExtractedDataJson { get; set; }
    public string? ClassifiedType { get; set; }
    public DateOnly? DocumentDate { get; set; }

    public List<JournalEntry> JournalEntries { get; set; } = [];
    public List<SupplierInvoice> SupplierInvoices { get; set; } = [];
    public List<CustomerInvoice> CustomerInvoices { get; set; } = [];
}
