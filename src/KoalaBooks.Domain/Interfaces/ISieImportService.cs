using jsiSIE;

namespace KoalaBooks.Domain.Interfaces;

public record SieImportPreview(
    string? CompanyName,
    string? OrgNumber,
    int SieType,
    List<SieImportFiscalYear> FiscalYears,
    int AccountCount,
    int VoucherCount);

public record SieImportFiscalYear(
    int RarId,
    DateOnly Start,
    DateOnly End,
    string Label,
    int VoucherCount,
    int BalanceCount,
    bool ExistsInDatabase,
    int? ExistingFiscalYearId);

public record SieImportResult(
    int FiscalYearId,
    int AccountsCreated,
    int AccountsUpdated,
    int EntriesImported,
    int LinesImported,
    int BalancesImported,
    string FiscalYearName,
    List<string> Warnings);

public record SieImportAllResult(
    List<SieImportResult> FiscalYears,
    int TotalAccountsCreated,
    int TotalEntriesImported,
    int TotalBalancesImported,
    List<string> Warnings);

public interface ISieImportService
{
    SieDocument Parse(Stream stream);
    Task<SieImportPreview> GetPreviewAsync(SieDocument doc);
    Task<SieImportAllResult> ImportAllAsync(SieDocument doc, bool overwrite);
    Task<SieImportResult> ImportFiscalYearAsync(SieDocument doc, int rarId, bool overwrite);
}
