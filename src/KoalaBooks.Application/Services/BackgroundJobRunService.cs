using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

// CreateRunAsync uses the ambient (Blazor-circuit-scoped) db so callers like
// DocumentService.UploadZipAsync can include the insert in their own already-open
// transaction. GetOpenRunsAsync/AcknowledgeAsync instead build a short-lived AppDbContext
// per call: they're driven by BackgroundJobStatusPoller's own independent polling timer,
// which runs on a separate schedule from whatever else the host page's own
// OnInitializedAsync/poll timer does against the SAME ambient db — since EF Core's
// DbContext isn't safe for concurrent operations and Blazor Server shares one scoped
// instance per circuit, two independently-timed pollers sharing it will eventually
// collide with "A second operation was started on this context instance..." (reproduced
// by hosting BackgroundJobStatusPoller on Inbox.razor, which already polls
// Document.ExtractionStatus on its own timer). See Microsoft's Blazor+EF Core guidance:
// use one context per operation when a component/service isn't already serializing DB
// access relative to the rest of the circuit.
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
