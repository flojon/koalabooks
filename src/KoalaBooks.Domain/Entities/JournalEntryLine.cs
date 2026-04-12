namespace KoalaBooks.Domain.Entities;

public class JournalEntryLine
{
    public int Id { get; set; }
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }

    public int JournalEntryId { get; set; }
    public JournalEntry JournalEntry { get; set; } = null!;

    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;
}
