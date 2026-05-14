namespace KoalaBooks.Domain.Entities;

public class JournalEntryAttachment
{
    public int Id { get; set; }
    public int JournalEntryId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public byte[] Data { get; set; } = [];
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
