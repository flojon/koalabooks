using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface IFiscalYearService
{
    Task<List<FiscalYear>> GetAllAsync();
    Task<FiscalYear?> GetByIdAsync(int id);
    Task<FiscalYear?> GetForDateAsync(DateOnly date);
    Task<FiscalYear?> GetDefaultFiscalYearAsync();
    Task<List<FiscalYear>> GetOpenFiscalYearsAsync();
    Task<(FiscalYear? FiscalYear, string? Error)> CreateAsync(FiscalYear fiscalYear);
    Task<List<Account>> GetAccountsAsync(int fiscalYearId);
    Task PropagateBalancesToNextYearAsync(int fiscalYearId);
}
