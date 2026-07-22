namespace KoalaBooks.Web.Models.Api;

public record VoucherGapExplanationResponse(
    int Id,
    int FiscalYearId,
    int MissingEntryNumber,
    string Explanation,
    DateTime ExplainedAt,
    string ExplainedBy);
