using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class BackgroundJobRunServiceTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task CreateRunAsync_CreatesPendingRunForCurrentOrganisation()
    {
        var svc = _fx.MakeBackgroundJobRunService();
        var run = await svc.CreateRunAsync(BackgroundJobType.SieImport, totalCount: 10);

        Assert.Equal(_fx.OrganisationId, run.OrganisationId);
        Assert.Equal(BackgroundJobType.SieImport, run.JobType);
        Assert.Equal(BackgroundJobStatus.Pending, run.Status);
        Assert.Equal(10, run.TotalCount);
        Assert.False(run.Acknowledged);
    }

    [Fact]
    public async Task GetOpenRunsAsync_ReturnsOnlyUnacknowledgedRunsOfMatchingType()
    {
        var svc = _fx.MakeBackgroundJobRunService();
        await svc.CreateRunAsync(BackgroundJobType.SieImport);
        var basRun = await svc.CreateRunAsync(BackgroundJobType.BasImport);
        var acknowledgedSieRun = await svc.CreateRunAsync(BackgroundJobType.SieImport);
        await svc.AcknowledgeAsync(acknowledgedSieRun.Id);

        var open = await svc.GetOpenRunsAsync(BackgroundJobType.SieImport);

        var run = Assert.Single(open);
        Assert.NotEqual(acknowledgedSieRun.Id, run.Id);
        Assert.NotEqual(basRun.Id, run.Id);
    }

    [Fact]
    public async Task GetOpenRunsAsync_DoesNotReturnAnotherOrganisationsRuns()
    {
        var svc = _fx.MakeBackgroundJobRunService();
        await svc.CreateRunAsync(BackgroundJobType.SieImport);

        // Same fixture/service pair, just switched to a different org — mirrors how
        // DocumentServiceTests' TenantIsolationTests-style checks work: MakeBackgroundJobRunService
        // binds to the fixture's own mutable LocalCurrentUser, so SetActiveTenant here changes
        // what both _fx.Db's query filter and svc's CreateRunAsync see, in lockstep.
        var otherOrg = new Organisation { Name = "Other Org", Slug = "other-org" };
        _fx.Db.Organisations.Add(otherOrg);
        await _fx.Db.SaveChangesAsync();
        _fx.SetActiveTenant(otherOrg.Id);

        var open = await svc.GetOpenRunsAsync(BackgroundJobType.SieImport);

        Assert.Empty(open);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsMatchingRun()
    {
        var svc = _fx.MakeBackgroundJobRunService();
        var run = await svc.CreateRunAsync(BackgroundJobType.SieImport, totalCount: 5);

        var found = await svc.GetByIdAsync(run.Id);

        Assert.NotNull(found);
        Assert.Equal(run.Id, found!.Id);
        Assert.Equal(BackgroundJobType.SieImport, found.JobType);
    }

    [Fact]
    public async Task GetByIdAsync_UnknownRunId_ReturnsNull()
    {
        var svc = _fx.MakeBackgroundJobRunService();
        var found = await svc.GetByIdAsync(999_999);
        Assert.Null(found);
    }

    [Fact]
    public async Task GetByIdAsync_AnotherOrganisationsRunId_ReturnsNull()
    {
        var svc = _fx.MakeBackgroundJobRunService();
        var run = await svc.CreateRunAsync(BackgroundJobType.SieImport);

        var otherOrg = new Organisation { Name = "Other Org", Slug = "other-org" };
        _fx.Db.Organisations.Add(otherOrg);
        await _fx.Db.SaveChangesAsync();
        _fx.SetActiveTenant(otherOrg.Id);

        var found = await svc.GetByIdAsync(run.Id);

        Assert.Null(found);
    }

    [Fact]
    public async Task AcknowledgeAsync_UnknownRunId_NoOpsWithoutThrowing()
    {
        var svc = _fx.MakeBackgroundJobRunService();
        await svc.AcknowledgeAsync(999_999);
    }

    [Fact]
    public async Task AcknowledgeAsync_AnotherOrganisationsRunId_NoOpsWithoutThrowing()
    {
        // Mirrors GetOpenRunsAsync_DoesNotReturnAnotherOrganisationsRuns: AcknowledgeAsync
        // reads through the same tenant-filtered db.BackgroundJobRuns DbSet, so switching
        // tenant mid-test should make the original org's run invisible to it rather than
        // throwing or acknowledging across tenants.
        var svc = _fx.MakeBackgroundJobRunService();
        var run = await svc.CreateRunAsync(BackgroundJobType.SieImport);
        var originalOrgId = _fx.OrganisationId;

        var otherOrg = new Organisation { Name = "Other Org", Slug = "other-org" };
        _fx.Db.Organisations.Add(otherOrg);
        await _fx.Db.SaveChangesAsync();
        _fx.SetActiveTenant(otherOrg.Id);

        await svc.AcknowledgeAsync(run.Id);

        _fx.SetActiveTenant(originalOrgId);
        var reloaded = await _fx.Db.BackgroundJobRuns.FirstAsync(r => r.Id == run.Id);
        Assert.False(reloaded.Acknowledged);
    }
}
