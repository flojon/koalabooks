namespace KoalaBooks.Domain.Interfaces;

public record MappingRow(
    string SourceAccountNumber,
    string SourceAccountName,
    decimal Ub,
    string? TargetAccountNumber);

public record ApplyMappingResult(int Mapped, int Skipped);

public interface IAccountMappingService
{
    Task<List<MappingRow>> BuildMappingAsync(int sourceFiscalYearId, int targetFiscalYearId);
    Task<ApplyMappingResult> ApplyMappingAsync(
        int sourceFiscalYearId,
        int targetFiscalYearId,
        List<MappingRow> rows);
}
