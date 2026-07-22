using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

// CreateRunAsync uses the ambient db so callers like UploadZipAsync can share their own
// transaction. GetOpenRunsAsync/AcknowledgeAsync build a short-lived AppDbContext instead,
// since they're driven by BackgroundJobStatusPoller's own timer running independently of
// whatever else the host page does against the same ambient, non-concurrency-safe context.
public class BackgroundJobRunService(
    AppDbContext db,
    DbContextOptions<AppDbContext> dbOptions,
    ICurrentUser currentUser) : IBackgroundJobRunService
{
    public async Task<BackgroundJobRun> CreateRunAsync(BackgroundJobType jobType, int? totalCount = null)
    {
        var run = new BackgroundJobRun
        {
            OrganisationId = currentUser.OrganisationId
                ?? throw new InvalidOperationException("No active tenant."),
            JobType = jobType,
            TotalCount = totalCount
        };
        db.BackgroundJobRuns.Add(run);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return run;
    }

    public async Task<List<BackgroundJobRun>> GetOpenRunsAsync(BackgroundJobType jobType)
    {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
        await using var freshDb = new AppDbContext(dbOptions, currentUser);
#pragma warning restore CA2007
        return await freshDb.BackgroundJobRuns
            .Where(r => r.JobType == jobType && !r.Acknowledged)
            .ToListAsync().ConfigureAwait(false);
    }

    // Callers polling a single run by id (e.g. a REST status-poll endpoint hit repeatedly
    // over independent requests) have the same staleness concern as GetOpenRunsAsync — a
    // fresh AppDbContext instead of the ambient ctor-injected one.
    public async Task<BackgroundJobRun?> GetByIdAsync(int runId)
    {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
        await using var freshDb = new AppDbContext(dbOptions, currentUser);
#pragma warning restore CA2007
        return await freshDb.BackgroundJobRuns.FirstOrDefaultAsync(r => r.Id == runId).ConfigureAwait(false);
    }

    public async Task AcknowledgeAsync(int runId)
    {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
        await using var freshDb = new AppDbContext(dbOptions, currentUser);
#pragma warning restore CA2007
        var run = await freshDb.BackgroundJobRuns.FirstOrDefaultAsync(r => r.Id == runId).ConfigureAwait(false);
        if (run is null) return;
        run.Acknowledged = true;
        await freshDb.SaveChangesAsync().ConfigureAwait(false);
    }
}
