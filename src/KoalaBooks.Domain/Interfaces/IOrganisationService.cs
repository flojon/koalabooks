using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface IOrganisationService
{
    Task<Organisation?> GetCurrentAsync();
    Task<string?> UpdateAsync(string name, string? orgNumber);
}
