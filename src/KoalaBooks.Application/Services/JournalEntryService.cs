using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class JournalEntryService
{
    private readonly AppDbContext _db;

    public JournalEntryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<JournalEntry>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from = null, DateOnly? to = null)
    {
        var query = _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Where(j => j.FiscalYearId == fiscalYearId);

        if (from.HasValue)
            query = query.Where(j => j.Date >= from.Value);
        if (to.HasValue)
            query = query.Where(j => j.Date <= to.Value);

        return await query.OrderBy(j => j.EntryNumber).ToListAsync();
    }

    public async Task<JournalEntry?> GetByIdAsync(int id)
    {
        return await _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Include(j => j.FiscalYear)
            .FirstOrDefaultAsync(j => j.Id == id);
    }

    public async Task<(JournalEntry? Entry, string? Error)> CreateAsync(JournalEntry entry)
    {
        var validationError = ValidateEntry(entry);
        if (validationError is not null)
            return (null, validationError);

        var fiscalYear = await _db.FiscalYears.FindAsync(entry.FiscalYearId);
        if (fiscalYear is null)
            return (null, "Fiscal year not found.");
        if (fiscalYear.IsClosed)
            return (null, "Cannot add entries to a closed fiscal year.");

        if (entry.Date < fiscalYear.StartDate || entry.Date > fiscalYear.EndDate)
            return (null, $"Entry date {entry.Date} is outside the fiscal year ({fiscalYear.StartDate} – {fiscalYear.EndDate}).");

        var fiscalYearAccountIds = await _db.Accounts
            .Where(a => a.FiscalYearId == entry.FiscalYearId)
            .Select(a => a.Id)
            .ToHashSetAsync();
        var invalidAccountIds = entry.Lines
            .Where(l => !fiscalYearAccountIds.Contains(l.AccountId))
            .Select(l => l.AccountId)
            .ToList();
        if (invalidAccountIds.Count > 0)
            return (null, "One or more line items reference accounts that do not exist in this fiscal year.");

        // Assign next entry number
        var maxNumber = await _db.JournalEntries
            .Where(j => j.FiscalYearId == entry.FiscalYearId)
            .MaxAsync(j => (int?)j.EntryNumber) ?? 0;
        entry.EntryNumber = maxNumber + 1;
        entry.CreatedAt = DateTime.UtcNow;

        _db.JournalEntries.Add(entry);
        await _db.SaveChangesAsync();
        return (entry, null);
    }

    public async Task<(JournalEntry? Entry, string? Error)> UpdateAsync(JournalEntry entry)
    {
        var validationError = ValidateEntry(entry);
        if (validationError is not null)
            return (null, validationError);

        var existing = await _db.JournalEntries
            .Include(j => j.Lines)
            .Include(j => j.FiscalYear)
            .FirstOrDefaultAsync(j => j.Id == entry.Id);

        if (existing is null)
            return (null, "Journal entry not found.");
        if (existing.IsPosted)
            return (null, "Cannot modify a posted journal entry. Create a reversal instead.");
        if (existing.FiscalYear.IsClosed)
            return (null, "Cannot modify entries in a closed fiscal year.");

        if (entry.Date < existing.FiscalYear.StartDate || entry.Date > existing.FiscalYear.EndDate)
            return (null, $"Entry date {entry.Date} is outside the fiscal year ({existing.FiscalYear.StartDate} – {existing.FiscalYear.EndDate}).");

        var fiscalYearAccountIds = await _db.Accounts
            .Where(a => a.FiscalYearId == existing.FiscalYearId)
            .Select(a => a.Id)
            .ToHashSetAsync();
        var invalidAccountIds = entry.Lines
            .Where(l => !fiscalYearAccountIds.Contains(l.AccountId))
            .Select(l => l.AccountId)
            .ToList();
        if (invalidAccountIds.Count > 0)
            return (null, "One or more line items reference accounts that do not exist in this fiscal year.");

        existing.Date = entry.Date;
        existing.Description = entry.Description;

        // Replace lines
        _db.JournalEntryLines.RemoveRange(existing.Lines);
        existing.Lines = entry.Lines;

        await _db.SaveChangesAsync();
        return (existing, null);
    }

    public async Task<string?> PostAsync(int entryId)
    {
        var entry = await _db.JournalEntries
            .Include(j => j.FiscalYear)
            .Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == entryId);
        if (entry is null)
            return "Journal entry not found.";
        if (entry.IsPosted)
            return "Journal entry is already posted.";
        if (entry.FiscalYear.IsClosed)
            return "Cannot post entries in a closed fiscal year.";

        entry.IsPosted = true;
        await _db.SaveChangesAsync();

        await PropagateAffectedAccountsAsync(
            entry.FiscalYearId, entry.Lines.Select(l => l.AccountId));
        return null;
    }

    public async Task<string?> DeleteDraftAsync(int entryId)
    {
        var entry = await _db.JournalEntries
            .Include(j => j.FiscalYear)
            .FirstOrDefaultAsync(j => j.Id == entryId);

        if (entry is null)
            return "Journal entry not found.";
        if (entry.IsPosted)
            return "Cannot delete a posted journal entry.";
        if (entry.FiscalYear.IsClosed)
            return "Cannot delete entries in a closed fiscal year.";

        _db.JournalEntries.Remove(entry);
        await _db.SaveChangesAsync();
        return null;
    }

    public async Task<(JournalEntry? Entry, string? Error)> CreateReversalAsync(int entryId, string reason)
    {
        var original = await _db.JournalEntries
            .Include(j => j.Lines)
            .Include(j => j.FiscalYear)
            .FirstOrDefaultAsync(j => j.Id == entryId);

        if (original is null)
            return (null, "Journal entry not found.");
        if (!original.IsPosted)
            return (null, "Can only reverse posted entries.");
        if (original.FiscalYear.IsClosed)
            return (null, "Cannot create reversals in a closed fiscal year.");

        var maxNumber = await _db.JournalEntries
            .Where(j => j.FiscalYearId == original.FiscalYearId)
            .MaxAsync(j => (int?)j.EntryNumber) ?? 0;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var reversalDate = today <= original.FiscalYear.EndDate && today >= original.FiscalYear.StartDate
            ? today
            : original.FiscalYear.EndDate;

        var reversal = new JournalEntry
        {
            EntryNumber = maxNumber + 1,
            FiscalYearId = original.FiscalYearId,
            Date = reversalDate,
            Description = $"Reversal of #{original.EntryNumber}: {reason}",
            CreatedAt = DateTime.UtcNow,
            IsPosted = true,
            Lines = original.Lines.Select(l => new JournalEntryLine
            {
                AccountId = l.AccountId,
                DebitAmount = l.CreditAmount,
                CreditAmount = l.DebitAmount
            }).ToList()
        };

        _db.JournalEntries.Add(reversal);
        await _db.SaveChangesAsync();

        await PropagateAffectedAccountsAsync(
            reversal.FiscalYearId, reversal.Lines.Select(l => l.AccountId));
        return (reversal, null);
    }

    public async Task<List<TrialBalanceRow>> GetTrialBalanceAsync(int fiscalYearId, bool excludeClosingEntries = true)
    {
        // Get all accounts for this fiscal year (includes IB)
        var accounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .ToDictionaryAsync(a => a.Id);

        // Get transaction totals per account (only posted entries)
        var lineQuery = _db.JournalEntryLines
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId)
            .Where(l => l.JournalEntry.IsPosted);
        if (excludeClosingEntries)
            lineQuery = lineQuery.Where(l => !l.JournalEntry.IsClosingEntry);

        var transactionTotals = await lineQuery
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Debit = g.Sum(l => l.DebitAmount), Credit = g.Sum(l => l.CreditAmount) })
            .ToListAsync();

        var rows = new List<TrialBalanceRow>();
        var accountsWithTransactions = new HashSet<int>();

        foreach (var t in transactionTotals)
        {
            accountsWithTransactions.Add(t.AccountId);
            if (accounts.TryGetValue(t.AccountId, out var account))
            {
                rows.Add(new TrialBalanceRow
                {
                    AccountNumber = account.AccountNumber,
                    AccountName = account.Name,
                    AccountClass = account.AccountClass,
                    IncomingBalance = account.IncomingBalance,
                    TotalDebit = t.Debit,
                    TotalCredit = t.Credit
                });
            }
        }

        // Include accounts with IB but no transactions
        foreach (var account in accounts.Values.Where(a => a.IncomingBalance != 0 && !accountsWithTransactions.Contains(a.Id)))
        {
            rows.Add(new TrialBalanceRow
            {
                AccountNumber = account.AccountNumber,
                AccountName = account.Name,
                AccountClass = account.AccountClass,
                IncomingBalance = account.IncomingBalance,
                TotalDebit = 0,
                TotalCredit = 0
            });
        }

        return rows.OrderBy(r => r.AccountNumber).ToList();
    }

    public async Task<GeneralLedgerAccountSection?> GetAccountLedgerAsync(
        int fiscalYearId, int accountId, DateOnly? from = null, DateOnly? to = null,
        bool excludeClosingEntries = true)
    {
        var account = await _db.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId && a.FiscalYearId == fiscalYearId);
        if (account is null) return null;

        var lineQuery = _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Where(l => l.AccountId == accountId)
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId)
            .Where(l => l.JournalEntry.IsPosted);

        if (excludeClosingEntries)
            lineQuery = lineQuery.Where(l => !l.JournalEntry.IsClosingEntry);

        if (from.HasValue)
            lineQuery = lineQuery.Where(l => l.JournalEntry.Date >= from.Value);
        if (to.HasValue)
            lineQuery = lineQuery.Where(l => l.JournalEntry.Date <= to.Value);

        var lines = await lineQuery
            .OrderBy(l => l.JournalEntry.Date)
            .ThenBy(l => l.JournalEntry.EntryNumber)
            .ToListAsync();

        var isCreditNormal = account.AccountClass.IsCreditNormal();
        var runningBalance = account.IncomingBalance;

        var section = new GeneralLedgerAccountSection
        {
            AccountNumber = account.AccountNumber,
            AccountName = account.Name,
            IncomingBalance = account.IncomingBalance
        };

        foreach (var line in lines)
        {
            runningBalance += isCreditNormal
                ? line.CreditAmount - line.DebitAmount
                : line.DebitAmount - line.CreditAmount;
            section.Rows.Add(new GeneralLedgerRow
            {
                Date = line.JournalEntry.Date,
                EntryNumber = line.JournalEntry.EntryNumber,
                Description = line.JournalEntry.Description,
                DebitAmount = line.DebitAmount,
                CreditAmount = line.CreditAmount,
                RunningBalance = runningBalance
            });
        }

        section.ClosingBalance = runningBalance;
        return section;
    }

    public async Task<List<GeneralLedgerAccountSection>> GetGeneralLedgerAsync(
        int fiscalYearId, string? fromAccount = null, string? toAccount = null,
        DateOnly? from = null, DateOnly? to = null, bool excludeClosingEntries = true,
        bool hideEmpty = false)
    {
        // Load all accounts for the FY first; do the ordinal range filter in memory.
        // string.Compare doesn't translate to SQL when the tenant query filter wraps
        // the query in a join.
        var allAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .ToListAsync();

        IEnumerable<Account> filtered = allAccounts;
        if (!string.IsNullOrWhiteSpace(fromAccount))
            filtered = filtered.Where(a => string.Compare(a.AccountNumber, fromAccount, StringComparison.Ordinal) >= 0);
        if (!string.IsNullOrWhiteSpace(toAccount))
            filtered = filtered.Where(a => string.Compare(a.AccountNumber, toAccount, StringComparison.Ordinal) <= 0);

        var accounts = filtered.OrderBy(a => a.AccountNumber, StringComparer.Ordinal).ToList();
        var accountIds = accounts.Select(a => a.Id).ToHashSet();

        var lineQuery = _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId)
            .Where(l => l.JournalEntry.IsPosted)
            .Where(l => accountIds.Contains(l.AccountId));

        if (excludeClosingEntries)
            lineQuery = lineQuery.Where(l => !l.JournalEntry.IsClosingEntry);

        if (from.HasValue)
            lineQuery = lineQuery.Where(l => l.JournalEntry.Date >= from.Value);
        if (to.HasValue)
            lineQuery = lineQuery.Where(l => l.JournalEntry.Date <= to.Value);

        var allLines = await lineQuery.ToListAsync();
        var linesByAccount = allLines.GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var sections = new List<GeneralLedgerAccountSection>();

        foreach (var account in accounts)
        {
            var section = new GeneralLedgerAccountSection
            {
                AccountNumber = account.AccountNumber,
                AccountName = account.Name,
                IncomingBalance = account.IncomingBalance
            };

            var isCreditNormal = account.AccountClass.IsCreditNormal();
            var runningBalance = account.IncomingBalance;

            if (linesByAccount.TryGetValue(account.Id, out var lines))
            {
                foreach (var line in lines.OrderBy(l => l.JournalEntry.Date).ThenBy(l => l.JournalEntry.EntryNumber))
                {
                    runningBalance += isCreditNormal
                        ? line.CreditAmount - line.DebitAmount
                        : line.DebitAmount - line.CreditAmount;
                    section.Rows.Add(new GeneralLedgerRow
                    {
                        Date = line.JournalEntry.Date,
                        EntryNumber = line.JournalEntry.EntryNumber,
                        Description = line.JournalEntry.Description,
                        DebitAmount = line.DebitAmount,
                        CreditAmount = line.CreditAmount,
                        RunningBalance = runningBalance
                    });
                }
            }

            section.ClosingBalance = runningBalance;

            if (hideEmpty && section.IncomingBalance == 0 && section.Rows.Count == 0)
                continue;

            sections.Add(section);
        }

        return sections;
    }

    public async Task<Dictionary<int, decimal>> GetComputedClosingBalancesAsync(int fiscalYearId)
    {
        var accounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .ToListAsync();

        var nets = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId && l.JournalEntry.IsPosted)
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Debit = g.Sum(l => l.DebitAmount), Credit = g.Sum(l => l.CreditAmount) })
            .ToListAsync();

        var netLookup = nets.ToDictionary(x => x.AccountId, x => (x.Debit, x.Credit));

        return accounts.ToDictionary(
            a => a.Id,
            a =>
            {
                var (debit, credit) = netLookup.TryGetValue(a.Id, out var n) ? n : (0m, 0m);
                return a.IncomingBalance + (a.AccountClass.IsCreditNormal()
                    ? credit - debit
                    : debit - credit);
            });
    }

    public async Task<HashSet<int>> GetAccountIdsWithTransactionsAsync(
        int fiscalYearId, DateOnly? from = null, DateOnly? to = null,
        bool includeClosingEntries = false)
    {
        var query = _db.JournalEntryLines
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId)
            .Where(l => l.JournalEntry.IsPosted);

        if (!includeClosingEntries)
            query = query.Where(l => !l.JournalEntry.IsClosingEntry);

        if (from.HasValue)
            query = query.Where(l => l.JournalEntry.Date >= from.Value);
        if (to.HasValue)
            query = query.Where(l => l.JournalEntry.Date <= to.Value);

        return await query.Select(l => l.AccountId).Distinct().ToHashSetAsync();
    }

    public async Task<List<BalanceSheetSection>> GetBalanceSheetAsync(int fiscalYearId, bool excludeClosingEntries = false)
    {
        var balanceClasses = new[] { AccountClass.Asset, AccountClass.Liability, AccountClass.Equity };

        var accounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId && balanceClasses.Contains(a.AccountClass))
            .ToListAsync();

        var lineQuery = _db.JournalEntryLines
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId)
            .Where(l => l.JournalEntry.IsPosted);
        if (excludeClosingEntries)
            lineQuery = lineQuery.Where(l => !l.JournalEntry.IsClosingEntry);

        var transactionTotals = await lineQuery
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Debit = g.Sum(l => l.DebitAmount), Credit = g.Sum(l => l.CreditAmount) })
            .ToDictionaryAsync(t => t.AccountId);

        var sectionDefs = new (string Title, AccountClass Class)[]
        {
            ("Tillgångar", AccountClass.Asset),
            ("Skulder", AccountClass.Liability),
            ("Eget kapital", AccountClass.Equity)
        };

        var sections = new List<BalanceSheetSection>();

        foreach (var (title, accountClass) in sectionDefs)
        {
            var isCreditNormal = accountClass.IsCreditNormal();
            var rows = new List<BalanceSheetRow>();

            foreach (var account in accounts.Where(a => a.AccountClass == accountClass).OrderBy(a => a.AccountNumber))
            {
                var debit = 0m;
                var credit = 0m;

                if (transactionTotals.TryGetValue(account.Id, out var totals))
                {
                    debit = totals.Debit;
                    credit = totals.Credit;
                }

                if (account.IncomingBalance == 0 && debit == 0 && credit == 0)
                    continue;

                var closingBalance = isCreditNormal
                    ? account.IncomingBalance + credit - debit
                    : account.IncomingBalance + debit - credit;

                rows.Add(new BalanceSheetRow
                {
                    AccountNumber = account.AccountNumber,
                    AccountName = account.Name,
                    IncomingBalance = account.IncomingBalance,
                    PeriodDebit = debit,
                    PeriodCredit = credit,
                    ClosingBalance = closingBalance
                });
            }

            sections.Add(new BalanceSheetSection
            {
                Title = title,
                Rows = rows,
                Total = rows.Sum(r => r.ClosingBalance)
            });
        }

        return sections;
    }

    public async Task<(List<IncomeStatementSection> Sections, decimal NetResult)> GetIncomeStatementAsync(
        int fiscalYearId, DateOnly? from = null, DateOnly? to = null, bool excludeClosingEntries = true)
    {
        // Load all P&L accounts so we can include IncomingBalance in the totals.
        // This makes the income statement consistent with YearEndClosingService,
        // which computes P&L balances as IB + transactions.
        var plAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .Where(a => a.AccountClass == AccountClass.Revenue || a.AccountClass == AccountClass.Expense)
            .ToListAsync();

        var lineQuery = _db.JournalEntryLines
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId)
            .Where(l => l.JournalEntry.IsPosted);

        if (excludeClosingEntries)
            lineQuery = lineQuery.Where(l => !l.JournalEntry.IsClosingEntry);

        if (from.HasValue)
            lineQuery = lineQuery.Where(l => l.JournalEntry.Date >= from.Value);
        if (to.HasValue)
            lineQuery = lineQuery.Where(l => l.JournalEntry.Date <= to.Value);

        var transactionTotals = await lineQuery
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Debit = g.Sum(l => l.DebitAmount), Credit = g.Sum(l => l.CreditAmount) })
            .ToDictionaryAsync(t => t.AccountId);

        // Include IB only for the full fiscal year view (no date filters).
        // Sub-period reports show only that period's transaction activity.
        bool includeIB = !from.HasValue && !to.HasValue;

        var revenueRows = new List<IncomeStatementRow>();
        var expenseRows = new List<IncomeStatementRow>();

        foreach (var account in plAccounts)
        {
            transactionTotals.TryGetValue(account.Id, out var totals);
            decimal debit = totals?.Debit ?? 0;
            decimal credit = totals?.Credit ?? 0;
            decimal ib = includeIB ? account.IncomingBalance : 0;

            decimal amount = account.AccountClass.IsCreditNormal()
                ? ib + credit - debit
                : ib + debit - credit;

            if (amount == 0)
                continue;

            var row = new IncomeStatementRow
            {
                AccountNumber = account.AccountNumber,
                AccountName = account.Name,
                Amount = amount
            };

            if (account.AccountClass == AccountClass.Revenue)
                revenueRows.Add(row);
            else
                expenseRows.Add(row);
        }

        revenueRows = revenueRows.OrderBy(r => r.AccountNumber).ToList();
        expenseRows = expenseRows.OrderBy(r => r.AccountNumber).ToList();

        var revenueTotal = revenueRows.Sum(r => r.Amount);
        var expenseTotal = expenseRows.Sum(r => r.Amount);

        var sections = new List<IncomeStatementSection>
        {
            new() { Title = "Intäkter", Rows = revenueRows, Total = revenueTotal },
            new() { Title = "Kostnader", Rows = expenseRows, Total = expenseTotal }
        };

        return (sections, revenueTotal - expenseTotal);
    }

    public async Task<VatReportData> GetVatReportAsync(int fiscalYearId, DateOnly? from = null, DateOnly? to = null)
    {
        // Narrow to 26xx in SQL (translates cleanly), then filter the exact 2610–2649
        // range client-side. string.Compare on the IQueryable doesn't translate when the
        // tenant query filter wraps the query in a join.
        var accounts26xx = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .Where(a => a.AccountNumber.StartsWith("26"))
            .ToListAsync();

        var vatAccounts = accounts26xx
            .Where(a => string.Compare(a.AccountNumber, "2610", StringComparison.Ordinal) >= 0
                     && string.Compare(a.AccountNumber, "2649", StringComparison.Ordinal) <= 0)
            .OrderBy(a => a.AccountNumber, StringComparer.Ordinal)
            .ToList();

        var vatAccountIds = vatAccounts.Select(a => a.Id).ToHashSet();

        var lineQuery = _db.JournalEntryLines
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId)
            .Where(l => l.JournalEntry.IsPosted)
            .Where(l => !l.JournalEntry.IsClosingEntry)
            .Where(l => vatAccountIds.Contains(l.AccountId));

        if (from.HasValue)
            lineQuery = lineQuery.Where(l => l.JournalEntry.Date >= from.Value);
        if (to.HasValue)
            lineQuery = lineQuery.Where(l => l.JournalEntry.Date <= to.Value);

        var transactionTotals = await lineQuery
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Debit = g.Sum(l => l.DebitAmount), Credit = g.Sum(l => l.CreditAmount) })
            .ToDictionaryAsync(t => t.AccountId);

        var outputRows = new List<VatReportRow>();
        var inputRows = new List<VatReportRow>();

        foreach (var account in vatAccounts)
        {
            transactionTotals.TryGetValue(account.Id, out var totals);
            var debit = totals?.Debit ?? 0;
            var credit = totals?.Credit ?? 0;

            if (debit == 0 && credit == 0)
                continue;

            var row = new VatReportRow
            {
                AccountNumber = account.AccountNumber,
                AccountName = account.Name,
                Debit = debit,
                Credit = credit
            };

            if (string.Compare(account.AccountNumber, "2640", StringComparison.Ordinal) >= 0)
                inputRows.Add(row);
            else
                outputRows.Add(row);
        }

        var outputTotal = outputRows.Sum(r => r.Credit - r.Debit);
        var inputTotal = inputRows.Sum(r => r.Debit - r.Credit);

        return new VatReportData
        {
            OutputVat = new VatReportSection { Title = "Utgående moms", Rows = outputRows, Total = outputTotal },
            InputVat = new VatReportSection { Title = "Ingående moms", Rows = inputRows, Total = inputTotal },
            NetPayable = outputTotal - inputTotal
        };
    }

    public async Task<DashboardStats> GetDashboardStatsAsync(int fiscalYearId)
    {
        var entryCount = await _db.JournalEntries
            .CountAsync(j => j.FiscalYearId == fiscalYearId);
        var totals = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId)
            .Where(l => l.JournalEntry.IsPosted)
            .GroupBy(_ => 1)
            .Select(g => new { Debit = g.Sum(l => l.DebitAmount), Credit = g.Sum(l => l.CreditAmount) })
            .FirstOrDefaultAsync();

        return new DashboardStats
        {
            EntryCount = entryCount,
            TotalDebit = totals?.Debit ?? 0,
            TotalCredit = totals?.Credit ?? 0
        };
    }

    private static string? ValidateEntry(JournalEntry entry)
    {
        if (entry.Lines.Count < 2)
            return "A journal entry must have at least 2 lines.";

        var totalDebit = entry.Lines.Sum(l => l.DebitAmount);
        var totalCredit = entry.Lines.Sum(l => l.CreditAmount);

        if (totalDebit != totalCredit)
            return $"Debit ({totalDebit:N2}) must equal Credit ({totalCredit:N2}).";

        if (entry.Lines.Any(l => l.DebitAmount < 0 || l.CreditAmount < 0))
            return "Amounts cannot be negative.";

        if (entry.Lines.Any(l => l.DebitAmount > 0 && l.CreditAmount > 0))
            return "A line cannot have both debit and credit amounts.";

        if (entry.Lines.Any(l => l.DebitAmount == 0 && l.CreditAmount == 0))
            return "Each line must have a debit or credit amount.";

        return null;
    }

    private async Task PropagateAffectedAccountsAsync(
        int fiscalYearId, IEnumerable<int> affectedAccountIds)
    {
        var nextYear = await _db.FiscalYears
            .FirstOrDefaultAsync(f => f.PreviousFiscalYearId == fiscalYearId);
        if (nextYear is null) return;

        var accountIdList = affectedAccountIds.ToList();

        var sourceAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId && accountIdList.Contains(a.Id))
            .ToListAsync();

        var sourceNumbers = sourceAccounts.Select(a => a.AccountNumber).ToHashSet();
        var nextAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == nextYear.Id && sourceNumbers.Contains(a.AccountNumber))
            .ToDictionaryAsync(a => a.AccountNumber);

        var debits = await _db.JournalEntryLines
            .Where(l => accountIdList.Contains(l.AccountId) && l.JournalEntry.IsPosted)
            .GroupBy(l => l.AccountId)
            .Select(g => new { g.Key, Total = g.Sum(l => l.DebitAmount) })
            .ToDictionaryAsync(x => x.Key, x => x.Total);

        var credits = await _db.JournalEntryLines
            .Where(l => accountIdList.Contains(l.AccountId) && l.JournalEntry.IsPosted)
            .GroupBy(l => l.AccountId)
            .Select(g => new { g.Key, Total = g.Sum(l => l.CreditAmount) })
            .ToDictionaryAsync(x => x.Key, x => x.Total);

        foreach (var account in sourceAccounts)
        {
            var isPnL = account.AccountClass is AccountClass.Revenue or AccountClass.Expense;
            if (isPnL) continue;

            var d = debits.GetValueOrDefault(account.Id);
            var c = credits.GetValueOrDefault(account.Id);
            var ub = account.AccountClass.IsCreditNormal()
                ? account.IncomingBalance + c - d
                : account.IncomingBalance + d - c;

            if (nextAccounts.TryGetValue(account.AccountNumber, out var nextAccount))
                nextAccount.IncomingBalance = ub;
        }

        await _db.SaveChangesAsync();
    }
}

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
