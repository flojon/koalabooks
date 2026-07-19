namespace KoalaBooks.Web.Models.Api;

public record ComputedBalanceResponse(int AccountId, decimal IncomingBalance, decimal ClosingBalance);
