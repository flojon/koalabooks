using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Entities;

public class Organisation
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? OrgNumber { get; set; }
    public LegalForm LegalForm { get; set; } = LegalForm.Aktiebolag;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
