namespace KoalaBooks.Web.Models.Api;

public record JournalEntryLineResponse(
    int Id,
    int AccountId,
    string AccountNumber,
    string AccountName,
    decimal DebitAmount,
    decimal CreditAmount);
