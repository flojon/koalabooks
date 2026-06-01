namespace KoalaBooks.Domain.Entities;

public class DocumentData
{
    public int DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public byte[] Data { get; set; } = [];
}
