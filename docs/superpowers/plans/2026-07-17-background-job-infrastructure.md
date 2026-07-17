# Shared Background-Job Infrastructure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the shared `BackgroundJobRun` status table, the job-side tenant-bootstrap helper, `BackgroundJobRunBase`, `IBackgroundJobRunService`, and the `BackgroundJobStatusPoller` Blazor component — the reusable pieces four upcoming jobs (#279 SIE import, #280 BAS import, #281 year-end close, #282 archive export) and the unmerged `ZipImportJob` (PR #251) will all build on, so none of them re-derive the tenant-bootstrap/poll-timer dance bespoke.

**Architecture:** A new `BackgroundJobRun` entity (generic status row: `OrganisationId`, `JobType`, `Status`, `ProcessedCount`/`TotalCount`, `ResultJson`, `Acknowledged`) replaces the batch/status-entity-per-job pattern. Job-side code (`KoalaBooks.Application.Jobs`) gets `JobTenantContext.CreateUnscoped` (the two-phase `LocalCurrentUser`/`AppDbContext` bootstrap `ZipImportJob` already does by hand) and an abstract `BackgroundJobRunBase` built on it that owns load/progress/complete lifecycle for any job with a `BackgroundJobRun` row. Page-side code goes through a new `IBackgroundJobRunService` (create/get-open/acknowledge) and a new `BackgroundJobStatusPoller` Razor component (extracted from `Inbox.razor`'s existing poll-timer block) instead of hand-rolling a `System.Threading.Timer`/staleness-cutoff per page. A Hangfire global `IApplyStateFilter` marks a run `Failed` when its underlying job exhausts retries — today that case leaves a job's status permanently stuck, unreported.

**Tech Stack:** ASP.NET Core / Blazor Server, EF Core (Npgsql), Hangfire (`Hangfire.Core`, already referenced by `KoalaBooks.Application`), xUnit + a real Postgres test container (`TestFixture`/`PostgresContainerFixture`), bUnit + NSubstitute for the Razor component test.

## Global Constraints

- Enum values are explicit ints, matching the codebase's existing style (e.g. `JournalEntryStatus`, `ExtractionStatus`).
- Scope is the design's sequencing step 1 only: the standalone shared-infra PR. It does **not** include retrofitting `ZipImportJob`/`ZipImportBatch` (PR #251) onto this — that's real rework on an unmerged branch and gets its own plan once this lands. No concrete `SieImportJob`/`BasImportJob`/etc. or their queue interfaces are created here either — those land with #279-282.
- `BackgroundJobType` still gets all five values (`ZipImport, SieImport, BasImport, YearEndClose, SieExport`) now, per the design doc — only the enum, not the queues/jobs that will use the latter four, needs to exist yet.
- **Deviation from the design doc's illustrative markup:** the design doc's `<BackgroundJobStatusPoller>` example passes an explicit `OrganisationId` parameter. This plan drops it — `IBackgroundJobRunService.GetOpenRunsAsync` is tenant-scoped via the request-scoped, DI-injected `ICurrentUser`/`AppDbContext` query filter, exactly like `Inbox.razor`'s existing `DocumentService.GetPendingAsync`/`GetOpenZipBatchesAsync` calls today — no page passes an org id explicitly anywhere else in this codebase, and doing so here would be redundant with (and could drift out of sync with) the tenant the query filter already derives correctly.
- **Deviation/clarification of the design doc's §4:** "Both `BackgroundJobRunBase` ... and the Blazor poller ... go through this [`IBackgroundJobRunService`]" is read here as applying to the **page/UI-side** operations only (create-on-upload, get-open-for-poller, acknowledge-from-poller). `BackgroundJobRunBase`'s own `LoadRunAsync`/progress/`CompleteAsync` operate directly on its own bootstrapped `AppDbContext.BackgroundJobRuns`, the same way `ZipImportJob.RunAsync` does today for `ZipImportBatch` — going through `IBackgroundJobRunService` isn't possible at `LoadRunAsync` time because the service's queries are tenant-filtered and the tenant (`Tenant.OrganisationId`) isn't known yet at that exact point (that's the entire reason `IgnoreQueryFilters()` bootstrap exists).
- `BackgroundJobRunBase` implements `IAsyncDisposable`, disposing its internally-constructed `AppDbContext`. This is safe without an explicit `await using` at each job's call site: every concrete job subclass is DI-registered `AddScoped<...Job>()` and resolved by Hangfire's ASP.NET Core activator from a per-invocation `IServiceScope`; that scope disposes any object it resolves which implements `IAsyncDisposable`/`IDisposable`, including the job instance itself (this is standard `Microsoft.Extensions.DependencyInjection` container behavior, independent of what the object does internally) — so `Db` gets disposed exactly once, when that job invocation's scope ends.
- Jobs and job-adjacent infrastructure (`JobTenantContext`, `BackgroundJobRunBase`, `BackgroundJobRunFailureFilter`) live in `KoalaBooks.Application/Jobs/` (namespace `KoalaBooks.Application.Jobs`), matching `DocumentExtractionJob`/`ZipImportJob`. `BackgroundJobRun` entity and `BackgroundJobType`/`BackgroundJobStatus` enums live in `KoalaBooks.Domain/Entities/` and `KoalaBooks.Domain/Enums/`. `IBackgroundJobRunService`/`BackgroundJobRunService` live in `KoalaBooks.Application/Services/`, matching `IDocumentService`/`DocumentService`. `BackgroundJobStatusPoller.razor` lives in `src/KoalaBooks.Components/Shared/`, matching `UnsavedChangesGuard.razor` and friends.
- Migrations: `dotnet ef migrations add <Name> --project src/KoalaBooks.Infrastructure --startup-project src/KoalaBooks.Web`, run from repo root.
- Design doc: `docs/superpowers/specs/2026-07-17-background-job-infrastructure-design.md`.

---

## Task 1: `BackgroundJobType`, `BackgroundJobStatus`, `BackgroundJobRun`

**Files:**
- Create: `src/KoalaBooks.Domain/Enums/BackgroundJobType.cs`
- Create: `src/KoalaBooks.Domain/Enums/BackgroundJobStatus.cs`
- Create: `src/KoalaBooks.Domain/Entities/BackgroundJobRun.cs`

**Interfaces:**
- Produces: `KoalaBooks.Domain.Enums.BackgroundJobType { ZipImport = 0, SieImport = 1, BasImport = 2, YearEndClose = 3, SieExport = 4 }`
- Produces: `KoalaBooks.Domain.Enums.BackgroundJobStatus { Pending = 0, Running = 1, Completed = 2, Failed = 3 }`
- Produces: `KoalaBooks.Domain.Entities.BackgroundJobRun` with `Id`, `OrganisationId`, `JobType`, `Status` (default `Pending`), `ProcessedCount`, `TotalCount` (nullable), `ResultJson` (nullable), `Acknowledged`, `CreatedAt`

- [ ] **Step 1: Create the enums**

```csharp
// src/KoalaBooks.Domain/Enums/BackgroundJobType.cs
namespace KoalaBooks.Domain.Enums;

public enum BackgroundJobType
{
    ZipImport = 0,
    SieImport = 1,
    BasImport = 2,
    YearEndClose = 3,
    SieExport = 4
}
```

```csharp
// src/KoalaBooks.Domain/Enums/BackgroundJobStatus.cs
namespace KoalaBooks.Domain.Enums;

public enum BackgroundJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}
```

- [ ] **Step 2: Create the entity**

```csharp
// src/KoalaBooks.Domain/Entities/BackgroundJobRun.cs
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Entities;

public class BackgroundJobRun
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public BackgroundJobType JobType { get; set; }
    public BackgroundJobStatus Status { get; set; } = BackgroundJobStatus.Pending;
    public int ProcessedCount { get; set; }
    public int? TotalCount { get; set; }          // null where progress isn't meaningful (e.g. BAS import)
    public string? ResultJson { get; set; }         // job-specific payload, read only by the page that knows its shape
    public bool Acknowledged { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build src/KoalaBooks.Domain/KoalaBooks.Domain.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Domain/Enums/BackgroundJobType.cs \
        src/KoalaBooks.Domain/Enums/BackgroundJobStatus.cs \
        src/KoalaBooks.Domain/Entities/BackgroundJobRun.cs
git commit -m "feat: add BackgroundJobRun entity and BackgroundJobType/BackgroundJobStatus enums"
```

---

## Task 2: `AppDbContext` wiring and migration

**Files:**
- Modify: `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`
- Create (auto-generated): `src/KoalaBooks.Infrastructure/Migrations/<timestamp>_AddBackgroundJobRuns.cs` (+ `.Designer.cs`)
- Modify (auto-generated): `src/KoalaBooks.Infrastructure/Migrations/AppDbContextModelSnapshot.cs`

**Interfaces:**
- Consumes: `BackgroundJobRun` (Task 1)
- Produces: `AppDbContext.BackgroundJobRuns : DbSet<BackgroundJobRun>`, a `BackgroundJobRuns` Postgres table with an FK to `Organisations` and a composite index on `(OrganisationId, JobType, Acknowledged)`

- [ ] **Step 1: Add the `DbSet`**

Modify `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs` — add after the `DocumentData` line (currently line 53):

```csharp
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentData> DocumentData => Set<DocumentData>();
    public DbSet<BackgroundJobRun> BackgroundJobRuns => Set<BackgroundJobRun>();
```

- [ ] **Step 2: Add the model configuration**

In the same file, add a new block at the end of `OnModelCreating`, right after the closing `});` of the `BankTransaction` block (currently ending at line 320, just before the final `}` of the method):

```csharp
        modelBuilder.Entity<BackgroundJobRun>(entity =>
        {
            entity.HasQueryFilter(r => _currentUser.OrganisationId != null && r.OrganisationId == _currentUser.OrganisationId);
            entity.HasOne<Organisation>()
                  .WithMany()
                  .HasForeignKey(r => r.OrganisationId)
                  .OnDelete(DeleteBehavior.Restrict);

            // The shape of the "open/unacknowledged runs for this org+JobType" query
            // IBackgroundJobRunService.GetOpenRunsAsync runs (filtered on Acknowledged,
            // not Status — a completed-but-unacknowledged run must still be returned so
            // the poller can fire OnRunCompleted for it), and that
            // BackgroundJobStatusPoller hits on every 5s tick for every open job on every
            // visible page.
            entity.HasIndex(r => new { r.OrganisationId, r.JobType, r.Acknowledged });
        });
```

- [ ] **Step 3: Generate the migration**

```bash
dotnet ef migrations add AddBackgroundJobRuns \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: new files appear under `src/KoalaBooks.Infrastructure/Migrations/`, and `AppDbContextModelSnapshot.cs` is updated. The generated `Up()` should contain a `CreateTable("BackgroundJobRuns", ...)`, a `CreateIndex` for the FK on `OrganisationId`, and a `CreateIndex` for the composite `(OrganisationId, JobType, Acknowledged)` index — no hand-editing needed (this is a brand-new table, no backfill).

- [ ] **Step 4: Build to confirm it compiles**

Run: `dotnet build src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Data/AppDbContext.cs \
        src/KoalaBooks.Infrastructure/Migrations/
git commit -m "feat: add BackgroundJobRuns table"
```

---

## Task 3: `JobTenantContext` and `BackgroundJobRunBase`

**Files:**
- Create: `src/KoalaBooks.Application/Jobs/JobTenantContext.cs`
- Create: `src/KoalaBooks.Application/Jobs/BackgroundJobRunBase.cs`
- Test: `tests/KoalaBooks.Tests/BackgroundJobRunBaseTests.cs`

**Interfaces:**
- Consumes: `AppDbContext` (`KoalaBooks.Infrastructure.Data`), `LocalCurrentUser` (`KoalaBooks.Domain`), `BackgroundJobRun`/`BackgroundJobType`/`BackgroundJobStatus` (Task 1), `AppDbContext.BackgroundJobRuns` (Task 2)
- Produces: `KoalaBooks.Application.Jobs.JobTenantContext.CreateUnscoped(DbContextOptions<AppDbContext> options) : (AppDbContext Db, LocalCurrentUser Tenant)`
- Produces: `KoalaBooks.Application.Jobs.BackgroundJobRunBase(DbContextOptions<AppDbContext> dbOptions) : IAsyncDisposable` with `protected Task<bool> LoadRunAsync(int runId)`, `protected Task SaveProgressAsync(int processedCount)`, `protected Task CompleteAsync(BackgroundJobStatus status, object? resultPayload)`, `protected AppDbContext Db`, `protected LocalCurrentUser Tenant`, `protected BackgroundJobRun Run`

- [ ] **Step 1: Implement `JobTenantContext`**

```csharp
// src/KoalaBooks.Application/Jobs/JobTenantContext.cs
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
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/KoalaBooks.Tests/BackgroundJobRunBaseTests.cs
using KoalaBooks.Application.Jobs;
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
        var updated = await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == run.Id);
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

        var updated = await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == run.Id);
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

        var updated = await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(BackgroundJobStatus.Completed, updated.Status);
        Assert.False(updated.Acknowledged);
        Assert.Contains("\"ImportedCount\":3", updated.ResultJson);
    }
}

file class TestJobRun(DbContextOptions<AppDbContext> dbOptions) : BackgroundJobRunBase(dbOptions)
{
    public Task<bool> LoadAsync(int runId) => LoadRunAsync(runId);
    public Task SaveProgress(int count) => SaveProgressAsync(count);
    public Task Complete(BackgroundJobStatus status, object? payload) => CompleteAsync(status, payload);
    public int? RunOrganisationId => Tenant.OrganisationId;
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter BackgroundJobRunBaseTests`
Expected: build error — `BackgroundJobRunBase` does not exist.

- [ ] **Step 4: Implement `BackgroundJobRunBase`**

```csharp
// src/KoalaBooks.Application/Jobs/BackgroundJobRunBase.cs
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter BackgroundJobRunBaseTests`
Expected: 5 passed.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Application/Jobs/JobTenantContext.cs \
        src/KoalaBooks.Application/Jobs/BackgroundJobRunBase.cs \
        tests/KoalaBooks.Tests/BackgroundJobRunBaseTests.cs
git commit -m "feat: add JobTenantContext and BackgroundJobRunBase"
```

---

## Task 4: `IBackgroundJobRunService` / `BackgroundJobRunService`

**Files:**
- Create: `src/KoalaBooks.Application/Services/IBackgroundJobRunService.cs`
- Create: `src/KoalaBooks.Application/Services/BackgroundJobRunService.cs`
- Modify: `src/KoalaBooks.Web/Program.cs`
- Modify: `tests/KoalaBooks.Tests/TestFixture.cs`
- Test: `tests/KoalaBooks.Tests/BackgroundJobRunServiceTests.cs`

**Interfaces:**
- Consumes: `AppDbContext` (`KoalaBooks.Infrastructure.Data`), `ICurrentUser` (`KoalaBooks.Domain.Interfaces`), `BackgroundJobRun`/`BackgroundJobType` (Task 1), `AppDbContext.BackgroundJobRuns` (Task 2)
- Produces: `KoalaBooks.Application.Services.IBackgroundJobRunService { Task<BackgroundJobRun> CreateRunAsync(BackgroundJobType jobType, int? totalCount = null); Task<List<BackgroundJobRun>> GetOpenRunsAsync(BackgroundJobType jobType); Task AcknowledgeAsync(int runId); }`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/KoalaBooks.Tests/BackgroundJobRunServiceTests.cs
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter BackgroundJobRunServiceTests`
Expected: build error — `BackgroundJobRunService` and `TestFixture.MakeBackgroundJobRunService` do not exist.

- [ ] **Step 3: Implement the interface and service**

```csharp
// src/KoalaBooks.Application/Services/IBackgroundJobRunService.cs
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Application.Services;

public interface IBackgroundJobRunService
{
    Task<BackgroundJobRun> CreateRunAsync(BackgroundJobType jobType, int? totalCount = null);
    Task<List<BackgroundJobRun>> GetOpenRunsAsync(BackgroundJobType jobType);
    Task AcknowledgeAsync(int runId);
}
```

```csharp
// src/KoalaBooks.Application/Services/BackgroundJobRunService.cs
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
```

Add a factory method to `tests/KoalaBooks.Tests/TestFixture.cs`, next to `MakeDocumentService` at the bottom of the class — this binds to the fixture's own private `_currentUser` field (the same instance `Db`'s query filter and `SetActiveTenant` use), the same way `MakeDocumentService(IDocumentStorage storage)` does, rather than a disconnected `LocalCurrentUser` that would drift out of sync with `Db` after a `SetActiveTenant` call:

```csharp
    public DocumentService MakeDocumentService(IDocumentExtractionQueue extractionQueue) =>
        new DocumentService(Db, new DbDocumentStorage(Db), extractionQueue, _currentUser);

    public BackgroundJobRunService MakeBackgroundJobRunService() =>
        new BackgroundJobRunService(Db, _currentUser);
```

(`KoalaBooks.Application.Services` — where `BackgroundJobRunService` lives — is already in `TestFixture.cs`'s using list, so no new `using` is needed.)

- [ ] **Step 4: Register the service in `Program.cs`**

Modify `src/KoalaBooks.Web/Program.cs` — add next to the other `AddScoped<IXxxService, XxxService>()` registrations (near line 165, right after `builder.Services.AddScoped<IDocumentService, DocumentService>();`):

```csharp
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IBackgroundJobRunService, BackgroundJobRunService>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter BackgroundJobRunServiceTests`
Expected: 5 passed.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Application/Services/IBackgroundJobRunService.cs \
        src/KoalaBooks.Application/Services/BackgroundJobRunService.cs \
        src/KoalaBooks.Web/Program.cs \
        tests/KoalaBooks.Tests/TestFixture.cs \
        tests/KoalaBooks.Tests/BackgroundJobRunServiceTests.cs
git commit -m "feat: add IBackgroundJobRunService"
```

---

## Task 5: `BackgroundJobStatusPoller` Blazor component

**Files:**
- Create: `src/KoalaBooks.Components/Shared/BackgroundJobStatusPoller.razor`
- Test: `tests/KoalaBooks.ComponentTests/BackgroundJobStatusPollerTests.cs`

**Interfaces:**
- Consumes: `IBackgroundJobRunService.GetOpenRunsAsync(BackgroundJobType)`, `.AcknowledgeAsync(int)` (Task 4), `BackgroundJobRun`/`BackgroundJobType`/`BackgroundJobStatus` (Task 1)
- Produces: `<BackgroundJobStatusPoller JobType="BackgroundJobType.X" StaleAfter="TimeSpan" PollInterval="TimeSpan" OnRunCompleted="EventCallback<BackgroundJobRun>" />`, a component with no rendered markup that owns polling/staleness/acknowledge

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/KoalaBooks.ComponentTests/BackgroundJobStatusPollerTests.cs
using KoalaBooks.Application.Services;
using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using NSubstitute.ReceivedExtensions;

namespace KoalaBooks.ComponentTests;

public class BackgroundJobStatusPollerTests : BunitContext
{
    private readonly IBackgroundJobRunService _service = Substitute.For<IBackgroundJobRunService>();

    public BackgroundJobStatusPollerTests()
    {
        Services.AddSingleton(_service);
    }

    [Fact]
    public void CompletedRun_InvokesCallbackAndAcknowledges()
    {
        var run = new BackgroundJobRun { Id = 1, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Completed, CreatedAt = DateTime.UtcNow };
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns([run]);

        BackgroundJobRun? completed = null;
        var cut = Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, r => completed = r)));

        cut.WaitForAssertion(() => Assert.NotNull(completed), TimeSpan.FromSeconds(2));
        Assert.Equal(1, completed!.Id);
        _ = _service.Received(1).AcknowledgeAsync(1);
    }

    [Fact]
    public void OpenNonStaleRun_KeepsPollingWithoutInvokingCallback()
    {
        // PollInterval overridden to 20ms so the timer actually ticks within this test's
        // lifetime — the real 5s default would make this test impractically slow. This
        // is what distinguishes "still polling" from "gave up": if UpdatePolling had
        // wrongly decided this run is stale (or terminal), GetOpenRunsAsync would only
        // ever be called once, from OnInitializedAsync, and this assertion would time out.
        var run = new BackgroundJobRun { Id = 2, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Running, CreatedAt = DateTime.UtcNow };
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns([run]);

        var invoked = false;
        var cut = Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.PollInterval, TimeSpan.FromMilliseconds(20))
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, _ => invoked = true)));

        cut.WaitForAssertion(
            () => _service.Received(Quantity.AtLeast(2)).GetOpenRunsAsync(BackgroundJobType.SieImport),
            TimeSpan.FromSeconds(2));
        Assert.False(invoked);
        _ = _service.DidNotReceive().AcknowledgeAsync(Arg.Any<int>());
    }

    [Fact]
    public async Task StaleRun_NeverStartsPollingAgainAfterInitialCheck()
    {
        // Older than StaleAfter at render time, so UpdatePolling's very first call (from
        // the initial PollAsync in OnInitializedAsync) must decide not to create a timer
        // at all. PollInterval is still overridden to 20ms — if UpdatePolling wrongly
        // scheduled a timer anyway, waiting past several intervals would catch it as a
        // second GetOpenRunsAsync call.
        var run = new BackgroundJobRun { Id = 3, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Running, CreatedAt = DateTime.UtcNow.AddMinutes(-20) };
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns([run]);

        var invoked = false;
        Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.StaleAfter, TimeSpan.FromMinutes(10))
            .Add(p => p.PollInterval, TimeSpan.FromMilliseconds(20))
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, _ => invoked = true)));

        await Task.Delay(150);
        _ = _service.Received(1).GetOpenRunsAsync(BackgroundJobType.SieImport);
        Assert.False(invoked);
    }

    [Fact]
    public void NoOpenRuns_CallsGetOpenRunsOnceOnInit()
    {
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns([]);

        Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, _ => { })));

        _ = _service.Received(1).GetOpenRunsAsync(BackgroundJobType.SieImport);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter BackgroundJobStatusPollerTests`
Expected: build error — `BackgroundJobStatusPoller` does not exist.

- [ ] **Step 3: Implement the component**

```razor
@* src/KoalaBooks.Components/Shared/BackgroundJobStatusPoller.razor
   Extracted from Inbox.razor's poll-timer/staleness/acknowledge block, generalized to
   any BackgroundJobType. Renders no markup — the host page owns all UI (toast text,
   download link, etc.) via OnRunCompleted; this component only owns the timer
   lifecycle, the staleness cutoff, and acknowledging a run once the host's callback
   returns. *@
@implements IDisposable
@using KoalaBooks.Application.Services
@using KoalaBooks.Domain.Entities
@using KoalaBooks.Domain.Enums

@code {
    [Parameter, EditorRequired] public BackgroundJobType JobType { get; set; }
    [Parameter] public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(10);
    [Parameter] public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    [Parameter, EditorRequired] public EventCallback<BackgroundJobRun> OnRunCompleted { get; set; }

    [Inject] private IBackgroundJobRunService BackgroundJobRunService { get; set; } = default!;

    private System.Threading.Timer? _pollTimer;
    private int _isPolling; // 0/1 guard, read/written across the timer thread and the
                             // dispatcher thread — needs Interlocked, not a plain bool.
    private bool _disposed;
    private List<BackgroundJobRun> _openRuns = [];

    protected override async Task OnInitializedAsync() => await PollAsync();

    private async Task PollAsync()
    {
        _openRuns = await BackgroundJobRunService.GetOpenRunsAsync(JobType);

        foreach (var run in _openRuns.Where(r => r.Status is BackgroundJobStatus.Completed or BackgroundJobStatus.Failed).ToList())
        {
            await OnRunCompleted.InvokeAsync(run);
            await BackgroundJobRunService.AcknowledgeAsync(run.Id);
            _openRuns.Remove(run);
        }

        UpdatePolling();
    }

    private void UpdatePolling()
    {
        var hasOpenRuns = _openRuns.Any(r => DateTime.UtcNow - r.CreatedAt < StaleAfter);
        if (hasOpenRuns)
        {
            _pollTimer ??= new System.Threading.Timer(OnPollTick, null, PollInterval, PollInterval);
        }
        else
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }

    private void OnPollTick(object? state)
    {
        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) return;
        _ = InvokeAsync(async () =>
        {
            try
            {
                if (_disposed) return;
                await PollAsync();
                if (!_disposed) StateHasChanged();
            }
            finally
            {
                Interlocked.Exchange(ref _isPolling, 0);
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        if (_pollTimer is not null)
        {
            using var waitHandle = new ManualResetEvent(false);
            _pollTimer.Dispose(waitHandle);
            waitHandle.WaitOne();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter BackgroundJobStatusPollerTests`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Components/Shared/BackgroundJobStatusPoller.razor \
        tests/KoalaBooks.ComponentTests/BackgroundJobStatusPollerTests.cs
git commit -m "feat: add BackgroundJobStatusPoller component"
```

---

## Task 6: `BackgroundJobRunFailureFilter`

**Files:**
- Create: `src/KoalaBooks.Application/Jobs/BackgroundJobRunFailureFilter.cs`
- Modify: `src/KoalaBooks.Web/Program.cs`
- Test: `tests/KoalaBooks.Tests/BackgroundJobRunFailureFilterTests.cs`

**Interfaces:**
- Consumes: `AppDbContext.BackgroundJobRuns` (Task 2), `BackgroundJobStatus` (Task 1), Hangfire's `IApplyStateFilter`/`ApplyStateContext`/`FailedState`/`IWriteOnlyTransaction` (from `Hangfire.Core`, already referenced)
- Produces: `KoalaBooks.Application.Jobs.BackgroundJobRunFailureFilter(IServiceScopeFactory scopeFactory) : IApplyStateFilter`, `internal static void BackgroundJobRunFailureFilter.MarkFailedIfOpen(AppDbContext db, int runId)`, `internal static bool BackgroundJobRunFailureFilter.TryExtractRunId(Hangfire.Common.Job job, out int runId)`

There's no existing `IApplyStateFilter` in this codebase and no Hangfire test infrastructure (Hangfire itself is excluded from the `Testing` environment — see `Program.cs`'s `if (!builder.Environment.IsEnvironment("Testing"))` guard around `AddHangfire`). Constructing a real `ApplyStateContext` in a unit test would mean faking several of Hangfire's internal types for no real gain, so this task separates the pure, testable logic from the thin `IApplyStateFilter` wiring around it: `MarkFailedIfOpen` (tested directly against a real `AppDbContext`) and `TryExtractRunId` (tested directly against `Hangfire.Common.Job` — a plain data class Hangfire itself constructs from a `Type`/`MethodInfo`/args, unlike `ApplyStateContext`, so it needs no faking). `TryExtractRunId` is where the actual correlation risk lives: scoping by `BackgroundJobRunBase` rather than just "first arg is an int" is what stops a failed `DocumentExtractionJob.RunAsync(int documentId)` from being mistaken for a `BackgroundJobRun`-based job whose `Id` happens to numerically collide with that `documentId` — `Document.Id` and `BackgroundJobRun.Id` are independent identity sequences, so collisions are routine, not edge cases. That risk gets its own regression tests below rather than being left to manual verification. Only the outermost `OnStateApplied` plumbing (the `NewState` check and DI scope resolution) is left to Task 7's manual run-through, the same way `HangfireDocumentExtractionQueue` was in the #208 plan.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/KoalaBooks.Tests/BackgroundJobRunFailureFilterTests.cs
using Hangfire.Common;
using KoalaBooks.Application.Jobs;
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter BackgroundJobRunFailureFilterTests`
Expected: build error — `BackgroundJobRunFailureFilter` does not exist.

- [ ] **Step 3: Implement the filter**

```csharp
// src/KoalaBooks.Application/Jobs/BackgroundJobRunFailureFilter.cs
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
```

- [ ] **Step 4: Register the filter in `Program.cs`**

Modify `src/KoalaBooks.Web/Program.cs` — the dashboard-mapping block (currently lines 231-237):

```csharp
if (!app.Environment.IsEnvironment("Testing"))
{
    app.MapHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [new HangfireDashboardAuthorizationFilter()]
    });
}
```

becomes:

```csharp
if (!app.Environment.IsEnvironment("Testing"))
{
    app.MapHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [new HangfireDashboardAuthorizationFilter()]
    });
    GlobalJobFilters.Filters.Add(new KoalaBooks.Application.Jobs.BackgroundJobRunFailureFilter(
        app.Services.GetRequiredService<IServiceScopeFactory>()));
}
```

(Guarded the same way as `MapHangfireDashboard` and `AddHangfire`/`AddHangfireServer` — Hangfire itself isn't configured at all in `Testing`, so there's nothing to observe there. `GlobalJobFilters` is already reachable via the existing `using Hangfire;` at the top of the file.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter BackgroundJobRunFailureFilterTests`
Expected: 6 passed.

- [ ] **Step 6: Build the Web project to confirm `Program.cs` compiles**

Run: `dotnet build src/KoalaBooks.Web/KoalaBooks.Web.csproj`
Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Application/Jobs/BackgroundJobRunFailureFilter.cs \
        src/KoalaBooks.Web/Program.cs \
        tests/KoalaBooks.Tests/BackgroundJobRunFailureFilterTests.cs
git commit -m "feat: mark BackgroundJobRun Failed when its Hangfire job exhausts retries"
```

---

## Task 7: Full-suite verification and a real run-through

**Files:** none (verification only)

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass (no regressions across `KoalaBooks.Tests` or `KoalaBooks.ComponentTests`).

- [ ] **Step 2: Verify the migration applies cleanly**

Use the project's `run` workflow to start the app (Postgres + Web) and confirm `BackgroundJobRuns` exists in the database after startup (the app auto-migrates on boot — see `Program.cs`'s `db.Database.MigrateAsync()` retry loop). No manual `dotnet ef database update` should be needed.

- [ ] **Step 3: Verify the failure filter end-to-end**

Since `BackgroundJobRunFailureFilter` has no automated test of its Hangfire wiring (Task 6), verify it manually:
1. Temporarily add a throwaway diagnostic job (or use the Hangfire dashboard's "Enqueue" / a `dotnet-script`/REPL snippet) that creates a `BackgroundJobRun` row via `IBackgroundJobRunService.CreateRunAsync`, then enqueues a Hangfire job whose `RunAsync(int runId)` always throws.
2. Watch `/hangfire` (as an Admin-role user) — confirm the job retries up to 3 times (`[AutomaticRetry(Attempts = 3)]` semantics, if applied to the throwaway job) and lands in `Failed`.
3. Query the `BackgroundJobRuns` table (or add a temporary debug endpoint) and confirm that row's `Status` is `Failed` and `Acknowledged` is `false`.
4. Remove the throwaway job/endpoint before finishing — this step is verification only, no permanent code should result from it.

Report any deviation from expected behavior before considering this done.

- [ ] **Step 4: Final commit if verification turned up fixes**

If Step 2 or 3 required any code changes, commit them separately with a clear message describing what was fixed; otherwise no commit needed for this task.
