using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface ICustomerService
{
    Task<List<Customer>> GetAllAsync(int organisationId);
    Task<Customer?> GetByIdAsync(int id);
    Task<(Customer? Customer, string? Error)> CreateAsync(Customer customer);
    Task<(Customer? Customer, string? Error)> UpdateAsync(Customer customer);
    Task<string?> DeactivateAsync(int customerId);
}
