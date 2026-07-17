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
    protected async Task<bool> LoadRunAsync(int runId)
    {
        var run = await Db.BackgroundJobRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runId).ConfigureAwait(false);
        if (run is null || run.Status is BackgroundJobStatus.Completed or BackgroundJobStatus.Failed)
            return false;

        Tenant.OrganisationId = run.OrganisationId;
        run.Status = BackgroundJobStatus.Running;
        await Db.SaveChangesAsync().ConfigureAwait(false);

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
