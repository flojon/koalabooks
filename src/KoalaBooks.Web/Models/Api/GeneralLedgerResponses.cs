namespace KoalaBooks.Web.Models.Api;

public record GeneralLedgerRowResponse(
    DateOnly Date,
    int EntryNumber,
    string Description,
    decimal DebitAmount,
    decimal CreditAmount,
    decimal RunningBalance);

public record GeneralLedgerAccountSectionResponse(
    string AccountNumber,
    string AccountName,
    decimal IncomingBalance,
    List<GeneralLedgerRowResponse> Rows,
    decimal ClosingBalance);
