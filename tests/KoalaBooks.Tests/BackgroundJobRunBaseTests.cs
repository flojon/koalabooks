using KoalaBooks.Application.Jobs;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class BackgroundJobRunBaseTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_fx.Db.Database.GetConnectionString()!).Options;

    [Fact]
    public async Task LoadRunAsync_PendingRun_SetsRunningAndBootstrapsTenant()
    {
        var run = new BackgroundJobRun { OrganisationId = _fx.OrganisationId, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Pending };
        _fx.Db.BackgroundJobRuns.Add(run);
        await _fx.Db.SaveChangesAsync();

        var job = new TestJobRun(Options());
        var loaded = await job.LoadAsync(run.Id);

        Assert.True(loaded);
        Assert.Equal(_fx.OrganisationId, job.RunOrganisationId);
        // Verify through a fresh DbContext — _fx.Db still has the stale tracked instance.
        await using var verifyDb1 = new AppDbContext(Options(), new LocalCurrentUser());
        var updated = await verifyDb1.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(BackgroundJobStatus.Running, updated.Status);
        await job.DisposeAsync();
    }

    [Fact]
    public async Task LoadRunAsync_AlreadyCompleted_ReturnsFalseAndLeavesStatusUnchanged()
    {
        var run = new BackgroundJobRun { OrganisationId = _fx.OrganisationId, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Completed };
        _fx.Db.BackgroundJobRuns.Add(run);
        await _fx.Db.SaveChangesAsync();

        var job = new TestJobRun(Options());
        var loaded = await job.LoadAsync(run.Id);

        Assert.False(loaded);
        var updated = await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(BackgroundJobStatus.Completed, updated.Status);
        await job.DisposeAsync();
    }

    [Fact]
    public async Task LoadRunAsync_UnknownRunId_ReturnsFalse()
    {
        var job = new TestJobRun(Options());
        var loaded = await job.LoadAsync(999_999);
        Assert.False(loaded);
        await job.DisposeAsync();
    }

    [Fact]
    public async Task SaveProgressAsync_PersistsProcessedCount()
    {
        var run = new BackgroundJobRun { OrganisationId = _fx.OrganisationId, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Pending };
        _fx.Db.BackgroundJobRuns.Add(run);
        await _fx.Db.SaveChangesAsync();

        var job = new TestJobRun(Options());
        await job.LoadAsync(run.Id);
        await job.SaveProgress(42);
        await job.DisposeAsync();

        // Verify through a fresh DbContext — _fx.Db still has the stale tracked instance.
        await using var verifyDb2 = new AppDbContext(Options(), new LocalCurrentUser());
        var updated = await verifyDb2.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(42, updated.ProcessedCount);
    }

    [Fact]
    public async Task CompleteAsync_SerializesResultAndClearsAcknowledged()
    {
        var run = new BackgroundJobRun { OrganisationId = _fx.OrganisationId, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Pending, Acknowledged = true };
        _fx.Db.BackgroundJobRuns.Add(run);
        await _fx.Db.SaveChangesAsync();

        var job = new TestJobRun(Options());
        await job.LoadAsync(run.Id);
        await job.Complete(BackgroundJobStatus.Completed, new { ImportedCount = 3 });
        await job.DisposeAsync();

        // Verify through a fresh DbContext — _fx.Db still has the stale tracked instance.
        await using var verifyDb3 = new AppDbContext(Options(), new LocalCurrentUser());
        var updated = await verifyDb3.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(BackgroundJobStatus.Completed, updated.Status);
        Assert.False(updated.Acknowledged);
        Assert.Contains("\"ImportedCount\":3", updated.ResultJson);
    }

    [Fact]
    public async Task LoadRunAsync_RunningRunSameJobId_ResumesSuccessfully()
    {
        // Simulates a Hangfire automatic retry: same job id re-invoking RunAsync after a
        // transient failure left the run Running. Must succeed so ProcessedCount resumes
        // instead of reprocessing from zero.
        var run = new BackgroundJobRun { OrganisationId = _fx.OrganisationId, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Running, ClaimedByJobId = "job-1", ProcessedCount = 7 };
        _fx.Db.BackgroundJobRuns.Add(run);
        await _fx.Db.SaveChangesAsync();

        var job = new TestJobRun(Options());
        var loaded = await job.LoadAsync(run.Id, "job-1");
        await job.DisposeAsync();

        Assert.True(loaded);
    }

    [Fact]
    public async Task LoadRunAsync_RunningRunDifferentJobId_ReturnsFalse()
    {
        // A second, independently-enqueued job trying to claim a run another job already
        // owns — the exact double-processing scenario a Status-only check can't catch.
        var run = new BackgroundJobRun { OrganisationId = _fx.OrganisationId, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Running, ClaimedByJobId = "job-1" };
        _fx.Db.BackgroundJobRuns.Add(run);
        await _fx.Db.SaveChangesAsync();

        var job = new TestJobRun(Options());
        var loaded = await job.LoadAsync(run.Id, "job-2");
        await job.DisposeAsync();

        Assert.False(loaded);
    }

    [Fact]
    public async Task LoadRunAsync_TwoJobsRaceAPendingRun_OnlyOneClaimsIt()
    {
        // Two independently-enqueued jobs (distinct Hangfire job ids) both calling
        // LoadRunAsync for the same still-Pending run — the actual race this fix closes.
        // Regardless of which one wins (the xmin token or the jobId check catches the
        // loser depending on exact timing), exactly one must end up owning the run.
        var run = new BackgroundJobRun { OrganisationId = _fx.OrganisationId, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Pending };
        _fx.Db.BackgroundJobRuns.Add(run);
        await _fx.Db.SaveChangesAsync();

        var jobA = new TestJobRun(Options());
        var jobB = new TestJobRun(Options());

        var results = await Task.WhenAll(
            jobA.LoadAsync(run.Id, "job-A"),
            jobB.LoadAsync(run.Id, "job-B"));

        await jobA.DisposeAsync();
        await jobB.DisposeAsync();

        Assert.Single(results, true);
        await using var verifyDb = new AppDbContext(Options(), new LocalCurrentUser());
        var updated = await verifyDb.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(BackgroundJobStatus.Running, updated.Status);
        Assert.Contains(updated.ClaimedByJobId, new[] { "job-A", "job-B" });
    }
}

file class TestJobRun(DbContextOptions<AppDbContext> dbOptions) : BackgroundJobRunBase(dbOptions)
{
    public Task<bool> LoadAsync(int runId, string jobId = "job-1") => LoadRunAsync(runId, jobId);
    public Task SaveProgress(int count) => SaveProgressAsync(count);
    public Task Complete(BackgroundJobStatus status, object? payload) => CompleteAsync(status, payload);
    public int? RunOrganisationId => Tenant.OrganisationId;
}
