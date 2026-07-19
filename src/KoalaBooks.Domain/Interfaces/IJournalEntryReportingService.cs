using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Interfaces;

public class TrialBalanceRow
{
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public AccountClass AccountClass { get; set; }
    public decimal IncomingBalance { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public decimal Balance => AccountClass.IsCreditNormal()
        ? IncomingBalance + TotalCredit - TotalDebit
        : IncomingBalance + TotalDebit - TotalCredit;
}

public class GeneralLedgerAccountSection
{
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public decimal IncomingBalance { get; set; }
    public List<GeneralLedgerRow> Rows { get; set; } = [];
    public decimal ClosingBalance { get; set; }
}

public class GeneralLedgerRow
{
    public DateOnly Date { get; set; }
    public int EntryNumber { get; set; }
    public string Description { get; set; } = "";
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public decimal RunningBalance { get; set; }
}

public class DashboardStats
{
    public int EntryCount { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
}

public class BalanceSheetSection
{
    public string Title { get; set; } = "";
    public List<BalanceSheetRow> Rows { get; set; } = [];
    public decimal Total { get; set; }
}

public class BalanceSheetRow
{
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public decimal IncomingBalance { get; set; }
    public decimal PeriodDebit { get; set; }
    public decimal PeriodCredit { get; set; }
    public decimal ClosingBalance { get; set; }
}

public class IncomeStatementSection
{
    public string Title { get; set; } = "";
    public List<IncomeStatementRow> Rows { get; set; } = [];
    public decimal Total { get; set; }
}

public class IncomeStatementRow
{
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public decimal Amount { get; set; }
}

public class VatReportData
{
    public VatReportSection OutputVat { get; set; } = new();
    public VatReportSection InputVat { get; set; } = new();
    public decimal NetPayable { get; set; }
}

public class VatReportSection
{
    public string Title { get; set; } = "";
    public List<VatReportRow> Rows { get; set; } = [];
    public decimal Total { get; set; }
}

public class VatReportRow
{
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}

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
