using KoalaBooks.Domain;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Jobs;

// The two-phase tenant bootstrap every BackgroundJobRun-based job needs: jobs have no
// HttpContext, so a DI-resolved ICurrentUser is always null and any tenant-scoped
// write/query would be rejected or filtered to nothing. Callers construct with no org
// set yet (so an initial IgnoreQueryFilters() lookup of the run row is unaffected either
// way), then set Tenant.OrganisationId once that row's org is known, so every subsequent
// query/write on the same Db scopes correctly from that point on.
public static class JobTenantContext
{
    public static (AppDbContext Db, LocalCurrentUser Tenant) CreateUnscoped(DbContextOptions<AppDbContext> options)
    {
        var tenant = new LocalCurrentUser();
        var db = new AppDbContext(options, tenant);
        return (db, tenant);
    }
}
