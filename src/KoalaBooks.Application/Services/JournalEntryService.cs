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

    public async Task<List<TrialBalanceRow>> GetTrialBalanceAsync(int fiscalYearId)
    {
        return await _db.JournalEntryLines
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId)
            .GroupBy(l => new { l.AccountId, l.Account.AccountNumber, l.Account.Name })
            .Select(g => new TrialBalanceRow
            {
                AccountNumber = g.Key.AccountNumber,
                AccountName = g.Key.Name,
                TotalDebit = g.Sum(l => l.DebitAmount),
                TotalCredit = g.Sum(l => l.CreditAmount)
            })
            .OrderBy(r => r.AccountNumber)
            .ToListAsync();
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
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public decimal Balance => TotalDebit - TotalCredit;
}

public class DashboardStats
{
    public int EntryCount { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
}
