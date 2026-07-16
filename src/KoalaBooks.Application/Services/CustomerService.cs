using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class CustomerService : ICustomerService
{
    private readonly AppDbContext _db;

    public CustomerService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Customer>> GetAllAsync(int organisationId)
    {
        return await _db.Customers
            .Where(c => c.OrganisationId == organisationId && c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task<(Customer? Customer, string? Error)> CreateAsync(Customer customer)
    {
        if (string.IsNullOrWhiteSpace(customer.Name))
            return (null, "Kundnamn är obligatoriskt.");

        customer.CreatedAt = DateTime.UtcNow;
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync().ConfigureAwait(false);
        return (customer, null);
    }

    public async Task<(Customer? Customer, string? Error)> UpdateAsync(Customer customer)
    {
        if (string.IsNullOrWhiteSpace(customer.Name))
            return (null, "Kundnamn är obligatoriskt.");

        var existing = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customer.Id).ConfigureAwait(false);
        if (existing is null) return (null, "Kunden hittades inte.");

        existing.Name = customer.Name;
        existing.OrgNumber = customer.OrgNumber;
        existing.Email = customer.Email;
        existing.Phone = customer.Phone;
        existing.Address = customer.Address;
        existing.PostalCode = customer.PostalCode;
        existing.City = customer.City;
        existing.Country = customer.Country;

        await _db.SaveChangesAsync().ConfigureAwait(false);
        return (existing, null);
    }

    public async Task<string?> DeactivateAsync(int customerId)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId).ConfigureAwait(false);
        if (customer is null) return "Kunden hittades inte.";

        var hasInvoices = await _db.CustomerInvoices.AnyAsync(i => i.CustomerId == customerId).ConfigureAwait(false);
        if (hasInvoices) return "Kunder med fakturor kan inte tas bort.";

        customer.IsActive = false;
        await _db.SaveChangesAsync().ConfigureAwait(false);
        return null;
    }
}
