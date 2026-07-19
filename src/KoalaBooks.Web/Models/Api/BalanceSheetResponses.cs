namespace KoalaBooks.Web.Models.Api;

public record BalanceSheetRowResponse(
    string AccountNumber,
    string AccountName,
    decimal IncomingBalance,
    decimal PeriodDebit,
    decimal PeriodCredit,
    decimal ClosingBalance);

public record BalanceSheetSectionResponse(string Title, List<BalanceSheetRowResponse> Rows, decimal Total);
