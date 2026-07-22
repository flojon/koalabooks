namespace KoalaBooks.Web.Models.Api;

public record BankTransactionPreviewResponse(
    int RowIndex,
    DateOnly? Date,
    decimal? Amount,
    string Description,
    string? Reference,
    bool IsDuplicate,
    string? ParseError);

public record ParsePreviewResponse(
    bool Success,
    string? Error,
    List<string> Headers,
    List<BankTransactionPreviewResponse> Previews);
