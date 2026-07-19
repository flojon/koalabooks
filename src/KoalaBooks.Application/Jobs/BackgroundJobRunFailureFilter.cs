using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Application.Jobs;

// Registered globally (GlobalJobFilters.Filters.Add in Program.cs), not as a per-method
// attribute — it needs to observe every job's terminal FailedState. Today, a
// BackgroundJobRun-based job that exhausts [AutomaticRetry(Attempts = 3)] leaves its run
// silently stuck at Running/Pending forever; this filter is the only place that gets
// reported at all.
public class BackgroundJobRunFailureFilter(IServiceScopeFactory scopeFactory) : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (context.NewState is not FailedState) return;
        if (!TryExtractRunId(context.BackgroundJob.Job, out var runId)) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        MarkFailedIfOpen(db, runId);
    }

    // Every BackgroundJobRun-based job (ZipImportJob and the four #279-282 jobs) inherits
    // BackgroundJobRunBase, and its RunAsync(int runId) takes the run id as its sole
    // argument, per the Enqueue(int runId) convention every Hangfire<Feature>Queue
    // follows. The Type check matters, not just the arg shape: other jobs — e.g.
    // DocumentExtractionJob.RunAsync(int documentId) — also take a single int argument,
    // and Document.Id/BackgroundJobRun.Id are independent identity sequences that will
    // routinely collide numerically. Without scoping by Type, a failed
    // DocumentExtractionJob could wrongly mark an unrelated open BackgroundJobRun Failed.
    internal static bool TryExtractRunId(Job job, out int runId)
    {
        runId = 0;
        if (!typeof(BackgroundJobRunBase).IsAssignableFrom(job.Type)) return false;
        if (job.Args.Count == 0 || job.Args[0] is not int id) return false;

        runId = id;
        return true;
    }

    internal static void MarkFailedIfOpen(AppDbContext db, int runId)
    {
        // IgnoreQueryFilters: this runs with no HttpContext, so ICurrentUser.OrganisationId
        // is always null and the tenant filter would hide every row — same rationale as
        // DocumentExtractionJob/BackgroundJobRunBase.
        var run = db.BackgroundJobRuns.IgnoreQueryFilters().FirstOrDefault(r => r.Id == runId);
        if (run is null || run.Status is BackgroundJobStatus.Completed or BackgroundJobStatus.Failed) return;

        run.Status = BackgroundJobStatus.Failed;
        run.Acknowledged = false;
        db.SaveChanges();
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction) { }
}
