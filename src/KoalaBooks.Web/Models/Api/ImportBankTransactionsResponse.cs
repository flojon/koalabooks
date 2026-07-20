namespace KoalaBooks.Web.Models.Api;

public record ImportBankTransactionsResponse(int Imported, int Skipped, int Duplicates, List<string> Errors);
