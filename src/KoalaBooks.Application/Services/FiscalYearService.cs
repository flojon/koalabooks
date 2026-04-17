using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class FiscalYearService
{
    private readonly AppDbContext _db;

    public FiscalYearService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<FiscalYear>> GetAllAsync()
    {
        return await _db.FiscalYears
            .OrderByDescending(f => f.StartDate)
            .ToListAsync();
    }

    public async Task<FiscalYear?> GetByIdAsync(int id)
    {
        return await _db.FiscalYears.FindAsync(id);
    }

    public async Task<FiscalYear?> GetActiveAsync()
    {
        return await _db.FiscalYears
            .Where(f => !f.IsClosed)
            .OrderByDescending(f => f.StartDate)
            .FirstOrDefaultAsync();
    }

    public async Task<FiscalYear> CreateAsync(FiscalYear fiscalYear)
    {
        _db.FiscalYears.Add(fiscalYear);
        await _db.SaveChangesAsync();

        // Copy accounts from the previous fiscal year with IB = previous UB
        await CopyAccountsFromPreviousYearAsync(fiscalYear);

        return fiscalYear;
    }

    public async Task CloseAsync(int id)
    {
        var fy = await _db.FiscalYears.FindAsync(id);
        if (fy is not null)
        {
            fy.IsClosed = true;
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Copies accounts from the previous fiscal year (by date) into the new year,
    /// setting IncomingBalance = previous year's OutgoingBalance.
    /// Only creates accounts that don't already exist in the target year.
    /// </summary>
    private async Task CopyAccountsFromPreviousYearAsync(FiscalYear targetYear)
    {
        var previousYear = await _db.FiscalYears
            .Where(f => f.EndDate < targetYear.StartDate && f.Id != targetYear.Id)
            .OrderByDescending(f => f.EndDate)
            .FirstOrDefaultAsync();

        if (previousYear is null) return;

        var previousAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == previousYear.Id)
            .ToListAsync();

        if (!previousAccounts.Any()) return;

        var existingNumbers = await _db.Accounts
            .Where(a => a.FiscalYearId == targetYear.Id)
            .Select(a => a.AccountNumber)
            .ToHashSetAsync();

        foreach (var prev in previousAccounts)
        {
            if (existingNumbers.Contains(prev.AccountNumber)) continue;

            var isPnL = prev.AccountClass is AccountClass.Revenue or AccountClass.Expense;

            _db.Accounts.Add(new Account
            {
                AccountNumber = prev.AccountNumber,
                Name = prev.Name,
                AccountClass = prev.AccountClass,
                IsActive = prev.IsActive,
                IncomingBalance = isPnL ? 0 : prev.OutgoingBalance,
                OutgoingBalance = 0,
                FiscalYearId = targetYear.Id
            });
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Propagates outgoing balances from the given fiscal year to the next year's
    /// incoming balances. Creates missing accounts in the next year if needed.
    /// </summary>
    public async Task PropagateBalancesToNextYearAsync(int fiscalYearId)
    {
        var sourceYear = await _db.FiscalYears.FindAsync(fiscalYearId);
        if (sourceYear is null) return;

        var nextYear = await _db.FiscalYears
            .Where(f => f.StartDate > sourceYear.EndDate)
            .OrderBy(f => f.StartDate)
            .FirstOrDefaultAsync();

        if (nextYear is null) return;

        var sourceAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .ToListAsync();

        var nextAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == nextYear.Id)
            .ToDictionaryAsync(a => a.AccountNumber);

        foreach (var src in sourceAccounts)
        {
            var isPnL = src.AccountClass is AccountClass.Revenue or AccountClass.Expense;
            var incomingBalance = isPnL ? 0 : src.OutgoingBalance;

            if (nextAccounts.TryGetValue(src.AccountNumber, out var nextAccount))
            {
                nextAccount.IncomingBalance = incomingBalance;
            }
            else if (src.OutgoingBalance != 0)
            {
                _db.Accounts.Add(new Account
                {
                    AccountNumber = src.AccountNumber,
                    Name = src.Name,
                    AccountClass = src.AccountClass,
                    IsActive = src.IsActive,
                    IncomingBalance = incomingBalance,
                    OutgoingBalance = 0,
                    FiscalYearId = nextYear.Id
                });
            }
        }

        await _db.SaveChangesAsync();
    }
}
