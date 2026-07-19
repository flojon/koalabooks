using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Application.Services;

public interface IFiscalYearService
{
    Task<List<FiscalYear>> GetAllAsync();
    Task<FiscalYear?> GetByIdAsync(int id);
    Task<FiscalYear?> GetActiveAsync();
    Task<FiscalYear?> GetForDateAsync(DateOnly date);
    Task<FiscalYear?> GetDefaultFiscalYearAsync();
    Task<List<FiscalYear>> GetOpenFiscalYearsAsync();
    Task<FiscalYear> CreateAsync(FiscalYear fiscalYear);
    Task<List<Account>> GetAccountsAsync(int fiscalYearId);
    Task PropagateBalancesToNextYearAsync(int fiscalYearId);
}
