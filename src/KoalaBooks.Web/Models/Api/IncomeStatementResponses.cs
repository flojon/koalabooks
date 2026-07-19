namespace KoalaBooks.Web.Models.Api;

public record IncomeStatementRowResponse(string AccountNumber, string AccountName, decimal Amount);

public record IncomeStatementSectionResponse(string Title, List<IncomeStatementRowResponse> Rows, decimal Total);

public record IncomeStatementResponse(List<IncomeStatementSectionResponse> Sections, decimal NetResult);
