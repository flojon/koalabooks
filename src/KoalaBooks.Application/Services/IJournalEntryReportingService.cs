namespace KoalaBooks.Application.Services;

public interface IJournalEntryReportingService
{
    Task<List<TrialBalanceRow>> GetTrialBalanceAsync(int fiscalYearId, bool excludeClosingEntries = true);
    Task<GeneralLedgerAccountSection?> GetAccountLedgerAsync(
        int fiscalYearId, int accountId, DateOnly? from = null, DateOnly? to = null,
        bool excludeClosingEntries = true);
    Task<List<GeneralLedgerAccountSection>> GetGeneralLedgerAsync(
        int fiscalYearId, string? fromAccount = null, string? toAccount = null,
        DateOnly? from = null, DateOnly? to = null, bool excludeClosingEntries = true,
        bool hideEmpty = false);
    Task<Dictionary<int, (decimal IB, decimal UB)>> GetComputedBalancesAsync(int fiscalYearId);
    Task<HashSet<int>> GetAccountIdsWithTransactionsAsync(
        int fiscalYearId, DateOnly? from = null, DateOnly? to = null,
        bool includeClosingEntries = false);
    Task<List<BalanceSheetSection>> GetBalanceSheetAsync(int fiscalYearId, bool excludeClosingEntries = false);
    Task<(List<IncomeStatementSection> Sections, decimal NetResult)> GetIncomeStatementAsync(
        int fiscalYearId, DateOnly? from = null, DateOnly? to = null, bool excludeClosingEntries = true);
    Task<VatReportData> GetVatReportAsync(int fiscalYearId, DateOnly? from = null, DateOnly? to = null);
    Task<DashboardStats> GetDashboardStatsAsync(int fiscalYearId);
}
