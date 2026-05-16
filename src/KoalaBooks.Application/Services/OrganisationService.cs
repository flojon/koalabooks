using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class OrganisationService
{
    private readonly AppDbContext _db;
    private readonly TenantContext _tenant;

    public OrganisationService(AppDbContext db, TenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<Organisation?> GetCurrentAsync()
    {
        if (_tenant.OrganisationId is null) return null;
        return await _db.Organisations.FirstOrDefaultAsync(o => o.Id == _tenant.OrganisationId);
    }

    public async Task<string?> UpdateAsync(string name, string? orgNumber)
    {
        if (_tenant.OrganisationId is null) return "Ingen organisation hittades.";
        var org = await _db.Organisations.FirstOrDefaultAsync(o => o.Id == _tenant.OrganisationId);
        if (org is null) return "Ingen organisation hittades.";

        if (string.IsNullOrWhiteSpace(name)) return "Namn är obligatoriskt.";

        org.Name = name.Trim();
        org.OrgNumber = string.IsNullOrWhiteSpace(orgNumber) ? null : orgNumber.Trim();
        await _db.SaveChangesAsync();
        return null;
    }
}
