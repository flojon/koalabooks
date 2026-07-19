namespace KoalaBooks.Domain.Interfaces;

public record ClosingValidationResult(bool IsValid, List<string> Errors);

public record ClosingPreview(
    bool IsValid,
    List<string> Errors,
    decimal TotalRevenue,
    decimal TotalExpenses,
    decimal NetResult,
    List<ClosingEntryPreview> Entries);

public record ClosingEntryPreview(string Description, List<ClosingLinePreview> Lines);

public record ClosingLinePreview(string AccountNumber, string AccountName, decimal Debit, decimal Credit);

public record ClosingResult(bool Success, string? Error, int? ClosingEntry1Number, int? ClosingEntry2Number);

public interface IYearEndClosingService
{
    Task<ClosingValidationResult> ValidateForClosingAsync(int fiscalYearId);
    Task<ClosingPreview> PreviewClosingAsync(int fiscalYearId);
    Task<ClosingResult> ExecuteClosingAsync(int fiscalYearId);
}
