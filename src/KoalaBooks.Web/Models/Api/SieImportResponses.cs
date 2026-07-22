namespace KoalaBooks.Web.Models.Api;

public record SieImportFiscalYearResponse(
    int RarId,
    DateOnly Start,
    DateOnly End,
    string Label,
    int VoucherCount,
    int BalanceCount,
    bool ExistsInDatabase,
    int? ExistingFiscalYearId);

public record SieImportPreviewResponse(
    string? CompanyName,
    string? OrgNumber,
    int SieType,
    List<SieImportFiscalYearResponse> FiscalYears,
    int AccountCount,
    int VoucherCount);

public record SieImportEnqueuedResponse(int RunId);
