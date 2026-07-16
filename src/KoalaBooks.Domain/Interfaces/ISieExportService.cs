namespace KoalaBooks.Domain.Interfaces;

public interface ISieExportService
{
    Task<byte[]> ExportAsync(int fiscalYearId, string? companyName = null);
}
