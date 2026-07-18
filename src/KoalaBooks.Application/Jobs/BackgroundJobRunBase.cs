using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace KoalaBooks.Application.Jobs;

// Constructed once per Hangfire job invocation (concrete jobs are DI-registered
// AddScoped<...Job>(), so a fresh instance — and a fresh Db/Tenant pair — is created per
// attempt/retry). Db is disposed via DisposeAsync when the DI scope that resolved this
// job instance ends: the container tracks disposal of any resolved service that
// implements IAsyncDisposable, which this class does, even though Db itself was
// constructed manually (via JobTenantContext.CreateUnscoped) rather than DI-registered.
public abstract class BackgroundJobRunBase : IAsyncDisposable
{
    protected AppDbContext Db { get; }
    protected LocalCurrentUser Tenant { get; }
    protected BackgroundJobRun Run { get; private set; } = null!;

    protected BackgroundJobRunBase(DbContextOptions<AppDbContext> dbOptions)
    {
        (Db, Tenant) = JobTenantContext.CreateUnscoped(dbOptions);
    }

    // IgnoreQueryFilters: Tenant.OrganisationId is still null at this point (see
    // JobTenantContext.CreateUnscoped), so the tenant query filter would otherwise hide
    // every row. Safe because runId is handed to the job by trusted code that just
    // created that exact row, not arbitrary tenant-crossing input.
    //
    // jobId (pass PerformContext.BackgroundJob.Id) distinguishes our own retry resuming a
    // Running run from a second, independently-enqueued job racing for the same one —
    // Status alone can't, since a legitimate retry also finds Running. A mismatched jobId
    // on a Running run is rejected outright; two jobs racing a still-Pending row are
    // resolved by the xmin token below (loser's SaveChangesAsync throws, returns false).
    protected async Task<bool> LoadRunAsync(int runId, string jobId)
    {
        var run = await Db.BackgroundJobRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runId).ConfigureAwait(false);
        if (run is null || run.Status is BackgroundJobStatus.Completed or BackgroundJobStatus.Failed)
            return false;
        if (run.Status == BackgroundJobStatus.Running && run.ClaimedByJobId != jobId)
            return false;

        Tenant.OrganisationId = run.OrganisationId;
        run.Status = BackgroundJobStatus.Running;
        run.ClaimedByJobId = jobId;

        try
        {
            await Db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }

        Run = run;
        return true;
    }

    // Incremental save-per-entry, same as ZipImportJob's current approach, so a Hangfire
    // retry resumes from ProcessedCount rather than reprocessing from zero.
    protected async Task SaveProgressAsync(int processedCount)
    {
        Run.ProcessedCount = processedCount;
        await Db.SaveChangesAsync().ConfigureAwait(false);
    }

    protected async Task CompleteAsync(BackgroundJobStatus status, object? resultPayload)
    {
        Run.ResultJson = resultPayload is null ? null : JsonSerializer.Serialize(resultPayload);
        Run.Status = status;
        Run.Acknowledged = false;
        await Db.SaveChangesAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
