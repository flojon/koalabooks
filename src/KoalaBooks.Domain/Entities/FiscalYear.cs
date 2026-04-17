namespace KoalaBooks.Domain.Entities;

public class FiscalYear
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsClosed { get; set; }
    public DateTime? ClosedAt { get; set; }
    public List<JournalEntry> JournalEntries { get; set; } = [];
    public List<Account> Accounts { get; set; } = [];
}
