using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class AccountService
{
    private readonly AppDbContext _db;

    public AccountService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Account>> GetAllAsync(int fiscalYearId)
    {
        return await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .OrderBy(a => a.AccountNumber)
            .ToListAsync();
    }

    public async Task<Account?> GetByIdAsync(int id)
    {
        return await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id);
    }

    public async Task<Account> CreateAsync(Account account)
    {
        var fiscalYearExists = await _db.FiscalYears.AnyAsync(f => f.Id == account.FiscalYearId);
        if (!fiscalYearExists) throw new InvalidOperationException("Fiscal year not found.");

        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();
        return account;
    }

    public async Task UpdateAsync(Account account)
    {
        _db.Accounts.Update(account);
        await _db.SaveChangesAsync();
    }

    public async Task ToggleActiveAsync(int id)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account is not null)
        {
            account.IsActive = !account.IsActive;
            await _db.SaveChangesAsync();
        }
    }

    public async Task<List<Account>> GetMissingFromSourceAsync(int currentFiscalYearId, int sourceFiscalYearId)
    {
        var existing = await _db.Accounts
            .Where(a => a.FiscalYearId == currentFiscalYearId)
            .Select(a => a.AccountNumber)
            .ToHashSetAsync();

        return await _db.Accounts
            .Where(a => a.FiscalYearId == sourceFiscalYearId && !existing.Contains(a.AccountNumber))
            .OrderBy(a => a.AccountNumber)
            .ToListAsync();
    }

    public async Task<int> CopyAccountsAsync(int targetFiscalYearId, List<int> sourceAccountIds)
    {
        var sources = await _db.Accounts
            .Where(a => sourceAccountIds.Contains(a.Id))
            .ToListAsync();

        var existing = await _db.Accounts
            .Where(a => a.FiscalYearId == targetFiscalYearId)
            .Select(a => a.AccountNumber)
            .ToHashSetAsync();

        var toAdd = sources
            .Where(s => !existing.Contains(s.AccountNumber))
            .Select(s => new Account
            {
                AccountNumber = s.AccountNumber,
                Name = s.Name,
                AccountClass = s.AccountClass,
                IsActive = true,
                FiscalYearId = targetFiscalYearId,
                IncomingBalance = s.OutgoingBalance
            })
            .ToList();

        if (toAdd.Count > 0)
        {
            _db.Accounts.AddRange(toAdd);
            await _db.SaveChangesAsync();
        }

        return toAdd.Count;
    }
}
