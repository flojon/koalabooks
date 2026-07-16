using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class OrganisationService : IOrganisationService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;

    public OrganisationService(AppDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<Organisation?> GetCurrentAsync()
    {
        if (_currentUser.OrganisationId is null) return null;
        return await _db.Organisations.FirstOrDefaultAsync(o => o.Id == _currentUser.OrganisationId).ConfigureAwait(false);
    }

    public async Task<string?> UpdateAsync(string name, string? orgNumber)
    {
        if (_currentUser.OrganisationId is null) return "Ingen organisation hittades.";
        var org = await _db.Organisations.FirstOrDefaultAsync(o => o.Id == _currentUser.OrganisationId).ConfigureAwait(false);
        if (org is null) return "Ingen organisation hittades.";

        if (string.IsNullOrWhiteSpace(name)) return "Namn är obligatoriskt.";

        org.Name = name.Trim();
        org.OrgNumber = string.IsNullOrWhiteSpace(orgNumber) ? null : orgNumber.Trim();
        await _db.SaveChangesAsync().ConfigureAwait(false);
        return null;
    }
}
