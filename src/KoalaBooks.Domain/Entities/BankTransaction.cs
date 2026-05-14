using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Entities;

public class BankTransaction
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public string? Reference { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public BankTransactionStatus Status { get; set; } = BankTransactionStatus.Unmatched;
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
}
