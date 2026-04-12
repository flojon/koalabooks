namespace KoalaBooks.Domain.Entities;

public class JournalEntry
{
    public int Id { get; set; }
    public int EntryNumber { get; set; }
    public DateOnly Date { get; set; }
    public required string Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int FiscalYearId { get; set; }
    public FiscalYear FiscalYear { get; set; } = null!;

    public List<JournalEntryLine> Lines { get; set; } = [];
}
