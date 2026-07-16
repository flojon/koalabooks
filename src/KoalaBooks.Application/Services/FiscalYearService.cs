using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class FiscalYearService : IFiscalYearService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;

    public FiscalYearService(AppDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<List<FiscalYear>> GetAllAsync()
    {
        return await _db.FiscalYears
            .OrderByDescending(f => f.StartDate)
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task<FiscalYear?> GetByIdAsync(int id)
    {
        return await _db.FiscalYears.FirstOrDefaultAsync(f => f.Id == id).ConfigureAwait(false);
    }

    public async Task<FiscalYear?> GetActiveAsync()
    {
        return await _db.FiscalYears
            .Where(f => !f.IsClosed)
            .OrderByDescending(f => f.StartDate)
            .FirstOrDefaultAsync().ConfigureAwait(false);
    }

    public async Task<FiscalYear> CreateAsync(FiscalYear fiscalYear)
    {
        fiscalYear.OrganisationId = _currentUser.OrganisationId
            ?? throw new InvalidOperationException("No active tenant.");

        var hasOverlap = await _db.FiscalYears
            .AnyAsync(f => f.StartDate <= fiscalYear.EndDate && f.EndDate >= fiscalYear.StartDate).ConfigureAwait(false);
        if (hasOverlap)
            throw new InvalidOperationException("The fiscal year overlaps with an existing fiscal year.");

        _db.FiscalYears.Add(fiscalYear);
        await _db.SaveChangesAsync().ConfigureAwait(false);

        // Copy accounts from the previous fiscal year with IB = previous UB
        await CopyAccountsFromPreviousYearAsync(fiscalYear).ConfigureAwait(false);

        return fiscalYear;
    }

    private async Task CopyAccountsFromPreviousYearAsync(FiscalYear targetYear)
    {
        var previousYear = await _db.FiscalYears
            .Where(f => f.EndDate < targetYear.StartDate && f.Id != targetYear.Id)
            .OrderByDescending(f => f.EndDate)
            .FirstOrDefaultAsync().ConfigureAwait(false);

        if (previousYear is null) return;

        targetYear.PreviousFiscalYearId = previousYear.Id;

        var previousAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == previousYear.Id)
            .ToListAsync().ConfigureAwait(false);

        if (!previousAccounts.Any())
        {
            await _db.SaveChangesAsync().ConfigureAwait(false);
            return;
        }

        var existingNumbers = await _db.Accounts
            .Where(a => a.FiscalYearId == targetYear.Id)
            .Select(a => a.AccountNumber)
            .ToHashSetAsync().ConfigureAwait(false);

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

        await _db.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task<List<Account>> GetAccountsAsync(int fiscalYearId)
    {
        return await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .OrderBy(a => a.AccountNumber)
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task PropagateBalancesToNextYearAsync(int fiscalYearId)
    {
        var sourceYear = await _db.FiscalYears.FirstOrDefaultAsync(f => f.Id == fiscalYearId).ConfigureAwait(false);
        if (sourceYear is null) return;

        // Prefer the explicitly linked year; fall back to next year by date.
        var nextYear = await _db.FiscalYears
                           .FirstOrDefaultAsync(f => f.PreviousFiscalYearId == fiscalYearId).ConfigureAwait(false)
                       ?? await _db.FiscalYears
                           .Where(f => f.StartDate > sourceYear.EndDate)
                           .OrderBy(f => f.StartDate)
                           .FirstOrDefaultAsync().ConfigureAwait(false);

        if (nextYear is null) return;

        var sourceAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .ToListAsync().ConfigureAwait(false);

        var nextAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == nextYear.Id)
            .ToDictionaryAsync(a => a.AccountNumber).ConfigureAwait(false);

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

        await _db.SaveChangesAsync().ConfigureAwait(false);
    }
}
