namespace KoalaBooks.Application.Services;

public interface IAccountMappingService
{
    Task<List<MappingRow>> BuildMappingAsync(int sourceFiscalYearId, int targetFiscalYearId);
    Task<ApplyMappingResult> ApplyMappingAsync(
        int sourceFiscalYearId,
        int targetFiscalYearId,
        List<MappingRow> rows);
}
