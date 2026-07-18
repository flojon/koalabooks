using Hangfire.Common;
using KoalaBooks.Application.Jobs;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class BackgroundJobRunFailureFilterTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task MarkFailedIfOpen_RunningRun_MarksFailedAndUnacknowledged()
    {
        var run = new BackgroundJobRun { OrganisationId = _fx.OrganisationId, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Running, Acknowledged = true };
        _fx.Db.BackgroundJobRuns.Add(run);
        await _fx.Db.SaveChangesAsync();

        BackgroundJobRunFailureFilter.MarkFailedIfOpen(_fx.Db, run.Id);

        var updated = await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(BackgroundJobStatus.Failed, updated.Status);
        Assert.False(updated.Acknowledged);
    }

    [Fact]
    public async Task MarkFailedIfOpen_AlreadyCompleted_LeavesUnchanged()
    {
        var run = new BackgroundJobRun { OrganisationId = _fx.OrganisationId, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Completed, Acknowledged = true };
        _fx.Db.BackgroundJobRuns.Add(run);
        await _fx.Db.SaveChangesAsync();

        BackgroundJobRunFailureFilter.MarkFailedIfOpen(_fx.Db, run.Id);

        var updated = await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(BackgroundJobStatus.Completed, updated.Status);
        Assert.True(updated.Acknowledged);
    }

    [Fact]
    public void MarkFailedIfOpen_UnknownRunId_NoOpsWithoutThrowing()
    {
        BackgroundJobRunFailureFilter.MarkFailedIfOpen(_fx.Db, 999_999);
    }

    [Fact]
    public async Task MarkFailedIfOpen_NullOrgDbContext_StillFindsAndMarksRun()
    {
        // Reproduces the actual production shape: Hangfire's IApplyStateFilter resolves
        // AppDbContext via DI with no HttpContext, so ICurrentUser.OrganisationId is null.
        // Without IgnoreQueryFilters(), BackgroundJobRun's tenant filter
        // (_currentUser.OrganisationId != null && ...) would hide every row and this
        // would silently no-op instead of marking the run Failed.
        var run = new BackgroundJobRun { OrganisationId = _fx.OrganisationId, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Running, Acknowledged = true };
        _fx.Db.BackgroundJobRuns.Add(run);
        await _fx.Db.SaveChangesAsync();

        await using var nullOrgDb = new AppDbContext(_fx.Options, new LocalCurrentUser());
        BackgroundJobRunFailureFilter.MarkFailedIfOpen(nullOrgDb, run.Id);

        // Verify through a fresh DbContext — _fx.Db still has the stale tracked instance
        // from the Add/SaveChangesAsync above.
        await using var verifyDb = new AppDbContext(_fx.Options, new LocalCurrentUser());
        var updated = await verifyDb.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(BackgroundJobStatus.Failed, updated.Status);
        Assert.False(updated.Acknowledged);
    }

    [Fact]
    public void TryExtractRunId_BackgroundJobRunBaseJob_ReturnsTrueWithRunId()
    {
        var method = typeof(FakeBackgroundJob).GetMethod(nameof(FakeBackgroundJob.RunAsync))!;
        var job = new Job(typeof(FakeBackgroundJob), method, 42);

        var found = BackgroundJobRunFailureFilter.TryExtractRunId(job, out var runId);

        Assert.True(found);
        Assert.Equal(42, runId);
    }

    [Fact]
    public void TryExtractRunId_NonBackgroundJobRunBaseJob_ReturnsFalse()
    {
        // Regression test for the runId/documentId collision hazard: DocumentExtractionJob
        // .RunAsync(int documentId) has the exact same (single int arg) shape as every
        // BackgroundJobRunBase job's RunAsync(int runId). Without scoping by Type, a
        // failed DocumentExtractionJob whose documentId happens to numerically match an
        // unrelated open BackgroundJobRun.Id would wrongly mark that run Failed.
        var method = typeof(FakeNonBackgroundJob).GetMethod(nameof(FakeNonBackgroundJob.RunAsync))!;
        var job = new Job(typeof(FakeNonBackgroundJob), method, 42);

        var found = BackgroundJobRunFailureFilter.TryExtractRunId(job, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryExtractRunId_BackgroundJobRunBaseJobWithNoArgs_ReturnsFalse()
    {
        var method = typeof(FakeBackgroundJob).GetMethod(nameof(FakeBackgroundJob.RunWithNoArgs))!;
        var job = new Job(typeof(FakeBackgroundJob), method);

        var found = BackgroundJobRunFailureFilter.TryExtractRunId(job, out _);

        Assert.False(found);
    }
}

file class FakeBackgroundJob(DbContextOptions<AppDbContext> dbOptions) : BackgroundJobRunBase(dbOptions)
{
    public Task RunAsync(int runId) => Task.CompletedTask;
    public Task RunWithNoArgs() => Task.CompletedTask;
}

file class FakeNonBackgroundJob
{
    // Mirrors DocumentExtractionJob.RunAsync(int documentId)'s shape on purpose — see
    // TryExtractRunId_NonBackgroundJobRunBaseJob_ReturnsFalse above.
    public Task RunAsync(int documentId) => Task.CompletedTask;
}
