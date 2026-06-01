namespace KoalaBooks.Web.Models.Api;

public class CreateJournalEntryLineRequest
{
    public int AccountId { get; init; }
    public decimal DebitAmount { get; init; }
    public decimal CreditAmount { get; init; }
}
