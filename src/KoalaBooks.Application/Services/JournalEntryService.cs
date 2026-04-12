using KoalaBooks.Domain.Entities;
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
        var entry = await _db.JournalEntries.FindAsync(entryId);
        if (entry is null)
            return "Journal entry not found.";
        if (entry.IsPosted)
            return "Journal entry is already posted.";

        entry.IsPosted = true;
        await _db.SaveChangesAsync();
        return null;
    }

    public async Task<(JournalEntry? Entry, string? Error)> CreateReversalAsync(int entryId, string reason)
    {
        var original = await _db.JournalEntries
            .Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == entryId);

        if (original is null)
            return (null, "Journal entry not found.");
        if (!original.IsPosted)
            return (null, "Can only reverse posted entries.");

        var maxNumber = await _db.JournalEntries
            .Where(j => j.FiscalYearId == original.FiscalYearId)
            .MaxAsync(j => (int?)j.EntryNumber) ?? 0;

        var reversal = new JournalEntry
        {
            EntryNumber = maxNumber + 1,
            FiscalYearId = original.FiscalYearId,
            Date = DateOnly.FromDateTime(DateTime.Today),
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
        return (reversal, null);
    }

    public async Task<List<TrialBalanceRow>> GetTrialBalanceAsync(int fiscalYearId)
    {
        // Get all accounts for this fiscal year (includes IB)
        var accounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .ToDictionaryAsync(a => a.Id);

        // Get transaction totals per account
        var transactionTotals = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId)
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
                IncomingBalance = account.IncomingBalance,
                TotalDebit = 0,
                TotalCredit = 0
            });
        }

        return rows.OrderBy(r => r.AccountNumber).ToList();
    }

    public async Task<List<GeneralLedgerAccountSection>> GetGeneralLedgerAsync(
        int fiscalYearId, string? fromAccount = null, string? toAccount = null,
        DateOnly? from = null, DateOnly? to = null)
    {
        var accountQuery = _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId);

        if (!string.IsNullOrWhiteSpace(fromAccount))
            accountQuery = accountQuery.Where(a => string.Compare(a.AccountNumber, fromAccount) >= 0);
        if (!string.IsNullOrWhiteSpace(toAccount))
            accountQuery = accountQuery.Where(a => string.Compare(a.AccountNumber, toAccount) <= 0);

        var accounts = await accountQuery.OrderBy(a => a.AccountNumber).ToListAsync();

        var lineQuery = _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId);

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

            var runningBalance = account.IncomingBalance;

            if (linesByAccount.TryGetValue(account.Id, out var lines))
            {
                foreach (var line in lines.OrderBy(l => l.JournalEntry.Date).ThenBy(l => l.JournalEntry.EntryNumber))
                {
                    runningBalance += line.DebitAmount - line.CreditAmount;
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
            sections.Add(section);
        }

        return sections;
    }

    public async Task<DashboardStats> GetDashboardStatsAsync(int fiscalYearId)
    {
        var entryCount = await _db.JournalEntries
            .CountAsync(j => j.FiscalYearId == fiscalYearId);
        var totals = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId)
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
}

public class TrialBalanceRow
{
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public decimal IncomingBalance { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public decimal Balance => IncomingBalance + TotalDebit - TotalCredit;
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
