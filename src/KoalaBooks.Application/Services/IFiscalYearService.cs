using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Application.Services;

public interface IFiscalYearService
{
    Task<List<FiscalYear>> GetAllAsync();
    Task<FiscalYear?> GetByIdAsync(int id);
    Task<FiscalYear?> GetActiveAsync();
    Task<FiscalYear> CreateAsync(FiscalYear fiscalYear);
    Task<List<Account>> GetAccountsAsync(int fiscalYearId);
    Task PropagateBalancesToNextYearAsync(int fiscalYearId);
}
