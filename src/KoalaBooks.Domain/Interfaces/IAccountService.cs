using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface IAccountService
{
    Task<List<Account>> GetAllAsync(int fiscalYearId);
    Task<Account?> GetByIdAsync(int id);
    Task<Account> CreateAsync(Account account);
    Task UpdateAsync(Account account);
    Task ToggleActiveAsync(int id);
    Task<List<Account>> GetMissingFromSourceAsync(int currentFiscalYearId, int sourceFiscalYearId);
    Task<int> CopyAccountsAsync(int targetFiscalYearId, List<int> sourceAccountIds);
}
