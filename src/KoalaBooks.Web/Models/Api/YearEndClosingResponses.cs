namespace KoalaBooks.Web.Models.Api;

public record ClosingValidationResponse(bool IsValid, List<string> Errors);

public record ClosingLinePreviewResponse(string AccountNumber, string AccountName, decimal Debit, decimal Credit);

public record ClosingEntryPreviewResponse(string Description, List<ClosingLinePreviewResponse> Lines);

public record ClosingPreviewResponse(
    bool IsValid,
    List<string> Errors,
    decimal TotalRevenue,
    decimal TotalExpenses,
    decimal NetResult,
    List<ClosingEntryPreviewResponse> Entries);

public record ClosingResultResponse(bool Success, string? Error, int? ClosingEntry1Number, int? ClosingEntry2Number);
