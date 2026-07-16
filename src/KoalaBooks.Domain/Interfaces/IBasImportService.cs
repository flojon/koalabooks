namespace KoalaBooks.Domain.Interfaces;

public record BasImportResult(int ImportedCount, int SkippedCount, List<string> Errors);

public interface IBasImportService
{
    Task<BasImportResult> ImportDefaultAsync(int fiscalYearId);
    Task<BasImportResult> ImportFromExcelAsync(Stream fileStream, int fiscalYearId);
}
