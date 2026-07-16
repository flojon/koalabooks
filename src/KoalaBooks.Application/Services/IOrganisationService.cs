using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Application.Services;

public interface IOrganisationService
{
    Task<Organisation?> GetCurrentAsync();
    Task<string?> UpdateAsync(string name, string? orgNumber);
}
