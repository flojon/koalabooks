using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class BackgroundJobRunService(AppDbContext db, ICurrentUser currentUser) : IBackgroundJobRunService
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

    public Task<List<BackgroundJobRun>> GetOpenRunsAsync(BackgroundJobType jobType) =>
        db.BackgroundJobRuns
            .Where(r => r.JobType == jobType && !r.Acknowledged)
            .ToListAsync();

    public async Task AcknowledgeAsync(int runId)
    {
        var run = await db.BackgroundJobRuns.FirstOrDefaultAsync(r => r.Id == runId).ConfigureAwait(false);
        if (run is null) return;
        run.Acknowledged = true;
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
