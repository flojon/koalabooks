namespace KoalaBooks.Application.Services;

public interface IYearEndClosingService
{
    Task<ClosingValidationResult> ValidateForClosingAsync(int fiscalYearId);
    Task<ClosingPreview> PreviewClosingAsync(int fiscalYearId);
    Task<ClosingResult> ExecuteClosingAsync(int fiscalYearId);
}
