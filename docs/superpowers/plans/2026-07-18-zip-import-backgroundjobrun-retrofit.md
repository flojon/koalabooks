# Zip Import → BackgroundJobRun Retrofit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retrofit `ZipImportJob`/`ZipImportBatch` (PR #251, still open/unmerged) onto the shared `BackgroundJobRun`/`BackgroundJobRunBase`/`BackgroundJobStatusPoller` infrastructure landed in PR #285 (merged into `main` 2026-07-18), per the design doc's Sequencing step 2 (`docs/superpowers/specs/2026-07-17-background-job-infrastructure-design.md`).

**Architecture:** `ZipImportBatch` (a bespoke status entity) is deleted entirely and replaced by a generic `BackgroundJobRun` row (`JobType = ZipImport`). `ZipImportJob` becomes a `BackgroundJobRunBase` subclass, using the shared `LoadRunAsync`/`SaveProgressAsync`/`CompleteAsync` lifecycle instead of hand-rolled tenant bootstrap and batch mutation. Zip-specific input (`FileName`, staging large-object `Oid`) no longer needs a persisted column — it flows through as ordinary Hangfire job arguments (serialized once at `Enqueue` time, replayed unchanged on every automatic retry), which is simpler than the old "look the batch row back up from the DB" approach. Zip-specific output (imported/skipped counts, skip reasons) flows through `BackgroundJobRun.ResultJson` as a `ZipImportResult` payload, which doubles as retry-safe *interim* progress storage (see Task 1, Step 4) so a resumed retry doesn't lose the tally from entries an earlier attempt already processed. `Inbox.razor`'s hand-rolled zip-batch poll/staleness/acknowledge block is replaced by `<BackgroundJobStatusPoller JobType="BackgroundJobType.ZipImport">`.

**Tech Stack:** .NET 10, EF Core (Npgsql), Hangfire (Postgres storage), Blazor Server (MudBlazor), xUnit + bUnit + NSubstitute.

## Global Constraints

- This is a retrofit onto an unmerged branch's own not-yet-shipped table (`ZipImportBatch`, added by PR #251's own migration `20260717084933_AddZipImportBatch`, never deployed to production). No backfill migration is needed or wanted — see Task 3.
- `BackgroundJobRunBase.LoadRunAsync(int runId, string jobId)` and the rest of the shared infra (`BackgroundJobRun`, `JobTenantContext`, `IBackgroundJobRunService`, `BackgroundJobStatusPoller`, `BackgroundJobRunFailureFilter`) are already implemented, tested, and merged to `main` via PR #285 — this plan only *consumes* them, it does not modify them.
- `BackgroundJobType.ZipImport` already exists in `src/KoalaBooks.Domain/Enums/BackgroundJobType.cs` — no enum change needed.
- Every `[AutomaticRetry(Attempts = 3)]` Hangfire job method that needs its own job id declares a trailing `PerformContext? context = null` parameter — Hangfire's special-parameter injection supplies the real `PerformContext` at execution time regardless of what's passed at enqueue time (this is the same mechanism documented for `IJobCancellationToken`/`CancellationToken`; verified against the current Hangfire docs and confirmed the type resolves from `Hangfire.Server.PerformContext` in this repo's referenced Hangfire.Core package). Enqueue call sites pass a literal `null` for it, exactly like the codebase's existing `JobCancellationToken.Null` convention would.
- No behavior change is intended for end users beyond what's structurally forced by the entity swap — the zip upload/import/toast UX (file size/entry limits, skip reasons, retry-resume correctness, toast wording) stays identical.
- Every step that changes code ends with a build and/or test run before moving to the next step; every task ends with a commit.

---

## Task 1: Retrofit `ZipImportJob`, `IZipImportQueue`, and `DocumentService` onto `BackgroundJobRun`

This is one coupled unit: `IZipImportQueue`'s signature, `ZipImportJob.RunAsync`'s signature, and `DocumentService`'s constructor all change together, so the solution can only compile again once all three (and every test that touches them) are updated in step. Steps are still ordered TDD-style (tests first, watch them fail to compile/fail on old shape, then implement) so review can follow the reasoning, even though the whole task lands as one commit.

**Files:**
- Modify: `src/KoalaBooks.Domain/Interfaces/IZipImportQueue.cs`
- Modify: `src/KoalaBooks.Application/Jobs/HangfireZipImportQueue.cs`
- Modify: `src/KoalaBooks.Application/Jobs/NoOpZipImportQueue.cs`
- Modify: `src/KoalaBooks.Application/Jobs/ZipImportJob.cs`
- Modify: `src/KoalaBooks.Application/Services/DocumentService.cs`
- Modify: `src/KoalaBooks.Application/Services/IDocumentService.cs`
- Modify: `tests/KoalaBooks.Tests/TestFixture.cs`
- Modify: `tests/KoalaBooks.Tests/ZipImportJobTests.cs`
- Modify: `tests/KoalaBooks.Tests/ZipImportJobRetryStrategyTests.cs`
- Modify: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`
- Modify: `tests/KoalaBooks.Tests/DocumentServiceZipRetryStrategyTests.cs`
- Modify: `tests/KoalaBooks.Tests/DocumentExtractionJobTests.cs`

**Interfaces:**
- Consumes (from PR #285, already on `main`): `BackgroundJobRunBase(DbContextOptions<AppDbContext> dbOptions)` exposing `protected AppDbContext Db`, `protected LocalCurrentUser Tenant`, `protected BackgroundJobRun Run`, `protected Task<bool> LoadRunAsync(int runId, string jobId)`, `protected Task SaveProgressAsync(int processedCount)`, `protected Task CompleteAsync(BackgroundJobStatus status, object? resultPayload)`; `IBackgroundJobRunService.CreateRunAsync(BackgroundJobType jobType, int? totalCount = null)` returning `Task<BackgroundJobRun>`; `BackgroundJobRunService(AppDbContext db, ICurrentUser currentUser)` (concrete class, constructible directly); `BackgroundJobRun.{Id, OrganisationId, JobType, Status, ProcessedCount, TotalCount, ResultJson, Acknowledged, CreatedAt}`.
- Produces (for Task 2): `BackgroundJobRun.ResultJson` on a `ZipImport`-type run deserializes (via plain `System.Text.Json`, property names matching) to `{ FileName: string, ImportedCount: int, SkippedCount: int, SkippedReasons: [{ FileName: string, Reason: string }] }` once `Status` is `Completed`.

- [ ] **Step 1: Update `IZipImportQueue`'s signature**

Replace the full contents of `src/KoalaBooks.Domain/Interfaces/IZipImportQueue.cs`:

```csharp
namespace KoalaBooks.Domain.Interfaces;

public interface IZipImportQueue
{
    void Enqueue(int runId, string fileName, uint stagingOid);
}
```

- [ ] **Step 2: Update `HangfireZipImportQueue` and `NoOpZipImportQueue`**

Replace the full contents of `src/KoalaBooks.Application/Jobs/HangfireZipImportQueue.cs`:

```csharp
using Hangfire;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class HangfireZipImportQueue(IBackgroundJobClient jobClient) : IZipImportQueue
{
    public void Enqueue(int runId, string fileName, uint stagingOid) =>
        jobClient.Enqueue<ZipImportJob>(job => job.RunAsync(runId, fileName, stagingOid, null));
}
```

Replace the full contents of `src/KoalaBooks.Application/Jobs/NoOpZipImportQueue.cs`:

```csharp
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class NoOpZipImportQueue : IZipImportQueue
{
    public void Enqueue(int runId, string fileName, uint stagingOid) { }
}
```

This alone will not compile yet — `ZipImportJob.RunAsync` still has the old `RunAsync(int batchId)` signature. That's fixed in Step 4.

- [ ] **Step 3: Rewrite `TestFixture.cs`'s `DocumentService`/`BackgroundJobRunService` factories**

`DocumentService`'s constructor gains an `IBackgroundJobRunService` parameter in Step 5 below — update the factories that build it now, so every test file that calls them compiles once Step 5 lands. In `tests/KoalaBooks.Tests/TestFixture.cs`, replace:

```csharp
    public DocumentService MakeDocumentService() =>
        MakeDocumentService(new DbDocumentStorage(Db));

    public DocumentService MakeDocumentService(IDocumentStorage storage) =>
        new DocumentService(Db, storage, new NoOpDocumentExtractionQueue(), new NoOpZipImportQueue(), _currentUser);

    public DocumentService MakeDocumentService(IDocumentExtractionQueue extractionQueue) =>
        new DocumentService(Db, new DbDocumentStorage(Db), extractionQueue, new NoOpZipImportQueue(), _currentUser);

    public DocumentService MakeDocumentService(IZipImportQueue zipImportQueue) =>
        new DocumentService(Db, new DbDocumentStorage(Db), new NoOpDocumentExtractionQueue(), zipImportQueue, _currentUser);

    public BackgroundJobRunService MakeBackgroundJobRunService() =>
        new BackgroundJobRunService(Db, _currentUser);
}
```

with:

```csharp
    public DocumentService MakeDocumentService() =>
        MakeDocumentService(new DbDocumentStorage(Db));

    public DocumentService MakeDocumentService(IDocumentStorage storage) =>
        new DocumentService(Db, storage, new NoOpDocumentExtractionQueue(), new NoOpZipImportQueue(), MakeBackgroundJobRunService(), _currentUser);

    public DocumentService MakeDocumentService(IDocumentExtractionQueue extractionQueue) =>
        new DocumentService(Db, new DbDocumentStorage(Db), extractionQueue, new NoOpZipImportQueue(), MakeBackgroundJobRunService(), _currentUser);

    public DocumentService MakeDocumentService(IZipImportQueue zipImportQueue) =>
        new DocumentService(Db, new DbDocumentStorage(Db), new NoOpDocumentExtractionQueue(), zipImportQueue, MakeBackgroundJobRunService(), _currentUser);

    public BackgroundJobRunService MakeBackgroundJobRunService() =>
        new BackgroundJobRunService(Db, _currentUser);
}
```

(Only the three `DocumentService` factory bodies change — `MakeBackgroundJobRunService` itself is untouched, just shown for anchoring the edit.)

- [ ] **Step 4: Rewrite `ZipImportJob.cs`**

Replace the full contents of `src/KoalaBooks.Application/Jobs/ZipImportJob.cs`:

```csharp
using Hangfire;
using Hangfire.Server;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.IO.Compression;
using System.Text.Json;

namespace KoalaBooks.Application.Jobs;

public record SkippedEntry(string FileName, string Reason);

// The ResultJson payload for a BackgroundJobRun with JobType == ZipImport. Written after
// every processed entry (not just at CompleteAsync) so a Hangfire retry that resumes
// mid-zip can recover the tally from entries an earlier attempt already accounted for —
// Run.ProcessedCount alone tells RunAsync *where* to resume, but not what the running
// import/skip counts were. Read directly by Inbox.razor via plain JSON property-name
// matching — no shared type reference between Components and Application.Jobs needed.
public record ZipImportResult(string FileName, int ImportedCount, int SkippedCount, List<SkippedEntry> SkippedReasons);

public class ZipImportJob(
    DbContextOptions<AppDbContext> dbOptions,
    IDocumentStorage storage,
    IDocumentExtractionQueue extractionQueue,
    IZipImportQueue zipImportQueue,
    ILogger<ZipImportJob> logger) : BackgroundJobRunBase(dbOptions)
{
    private static readonly Dictionary<string, string> ZipEntryContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };

    // A run left un-Completed after all 3 retries are exhausted is caught by
    // BackgroundJobRunFailureFilter (PR #285), which marks it Failed so
    // BackgroundJobStatusPoller/Inbox.razor can surface that instead of polling forever.
    //
    // fileName/stagingOid arrive as ordinary Hangfire job arguments (serialized once at
    // Enqueue time, replayed unchanged on every automatic retry) rather than being looked
    // up from a persisted row — BackgroundJobRun has no room for job-specific input
    // columns by design (see the design doc's Architecture §1).
    //
    // context is Hangfire's special PerformContext parameter: real callers (see
    // HangfireZipImportQueue) pass null at enqueue time and Hangfire substitutes the real
    // context at execution time, giving BackgroundJob.Id — the same id across every
    // automatic retry of this job, which is what LoadRunAsync's double-claim protection
    // keys on. Unit tests call RunAsync directly with no context, falling back to a fresh
    // random id; no test depends on jobId being stable across two separate RunAsync calls
    // (retry-resume tests simulate the earlier attempt by writing directly to the row).
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(int runId, string fileName, uint stagingOid, PerformContext? context = null)
    {
        var jobId = context?.BackgroundJob.Id ?? Guid.NewGuid().ToString();
        if (!await LoadRunAsync(runId, jobId).ConfigureAwait(false)) return;

        var documentService = new DocumentService(
            Db, storage, extractionQueue, zipImportQueue, new BackgroundJobRunService(Db, Tenant), Tenant);

        var (importedCount, skippedCount, skipped) = Run.ResultJson is null
            ? (0, 0, new List<SkippedEntry>())
            : ToTuple(JsonSerializer.Deserialize<ZipImportResult>(Run.ResultJson)!);

        var tempPath = Path.GetTempFileName();
        try
        {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
            await using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite))
#pragma warning restore CA2007
            {
                var readStrategy = Db.Database.CreateExecutionStrategy();
                await readStrategy.ExecuteAsync(async () =>
                {
                    // A retry re-invokes this whole delegate: reset the temp file so a
                    // prior attempt's partial write (from a transient failure mid-copy)
                    // can't leave leftover bytes ahead of this attempt's data.
                    tempStream.Position = 0;
                    tempStream.SetLength(0);
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                    await using var tx = await Db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
                    var conn = (NpgsqlConnection)Db.Database.GetDbConnection();
                    await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, stagingOid, tempStream).ConfigureAwait(false);
                    await tx.CommitAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
                tempStream.Position = 0;

                ZipArchive? archive = null;
                try
                {
                    try
                    {
                        archive = new ZipArchive(tempStream, ZipArchiveMode.Read, leaveOpen: true);
                    }
                    catch (InvalidDataException)
                    {
                        skipped.Add(new SkippedEntry("(zip-fil)", "Ogiltig zip-fil."));
                        skippedCount++;
                        await CompleteAsync(BackgroundJobStatus.Completed,
                            new ZipImportResult(fileName, importedCount, skippedCount, skipped)).ConfigureAwait(false);
                        await DeleteStagingAsync(stagingOid, runId).ConfigureAwait(false);
                        return;
                    }

                    var fileEntries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

                    foreach (var entry in fileEntries.Skip(Run.ProcessedCount))
                    {
                        if (!ZipEntryContentTypes.TryGetValue(Path.GetExtension(entry.Name), out var contentType))
                        {
                            skipped.Add(new SkippedEntry(entry.Name, "Otillåten filtyp."));
                            skippedCount++;
                        }
                        else
                        {
                            try
                            {
                                var entryFullName = entry.FullName;
                                var (doc, err) = await documentService.UploadAsync(
                                    entry.Name, contentType, () => archive.GetEntry(entryFullName)!.Open()).ConfigureAwait(false);
                                if (doc is not null)
                                {
                                    importedCount++;
                                }
                                else
                                {
                                    skipped.Add(new SkippedEntry(entry.Name, err ?? "Okänt fel."));
                                    skippedCount++;
                                }
                            }
                            catch (InvalidDataException)
                            {
                                skipped.Add(new SkippedEntry(entry.Name, "Skadad fil."));
                                skippedCount++;
                            }
                        }

                        // Written before SaveProgressAsync's own SaveChangesAsync so both
                        // land in the same round trip — see the ZipImportResult doc
                        // comment on why this interim write matters for retry-resume.
                        Run.ResultJson = JsonSerializer.Serialize(new ZipImportResult(fileName, importedCount, skippedCount, skipped));
                        await SaveProgressAsync(Run.ProcessedCount + 1).ConfigureAwait(false);
                    }
                }
                finally
                {
                    archive?.Dispose();
                }
            }

            await CompleteAsync(BackgroundJobStatus.Completed,
                new ZipImportResult(fileName, importedCount, skippedCount, skipped)).ConfigureAwait(false);
            await DeleteStagingAsync(stagingOid, runId).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static (int, int, List<SkippedEntry>) ToTuple(ZipImportResult r) => (r.ImportedCount, r.SkippedCount, r.SkippedReasons);

    private async Task DeleteStagingAsync(uint stagingOid, int runId)
    {
        try
        {
            var strategy = Db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tx = await Db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
                var conn = (NpgsqlConnection)Db.Database.GetDbConnection();
                await PostgresLargeObjects.DeleteLargeObjectAsync(conn, stagingOid).ConfigureAwait(false);
                await tx.CommitAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The run has already been marked Completed and the local temp file cleaned
            // up — a leaked LO here is a minor storage-cleanup miss, not a correctness
            // issue for the run itself, so log and move on rather than failing the job.
            logger.LogWarning(ex, "Failed to delete staging large object {Oid} for run {RunId}", stagingOid, runId);
        }
    }
}
```

- [ ] **Step 5: Update `DocumentService.cs`**

In `src/KoalaBooks.Application/Services/DocumentService.cs`, rename `UploadZipAsync`'s return tuple field to match the `IDocumentService` rename below. Replace:

```csharp
    public async Task<(int? BatchId, string? Error)> UploadZipAsync(string fileName, Func<Stream> openZipData)
```

with:

```csharp
    public async Task<(int? RunId, string? Error)> UploadZipAsync(string fileName, Func<Stream> openZipData)
```

Update the constructor. Replace:

```csharp
public class DocumentService(
    AppDbContext db,
    IDocumentStorage storage,
    IDocumentExtractionQueue extractionQueue,
    IZipImportQueue zipImportQueue,
    ICurrentUser currentUser) : IDocumentService
```

with:

```csharp
public class DocumentService(
    AppDbContext db,
    IDocumentStorage storage,
    IDocumentExtractionQueue extractionQueue,
    IZipImportQueue zipImportQueue,
    IBackgroundJobRunService backgroundJobRunService,
    ICurrentUser currentUser) : IDocumentService
```

Remove `GetOpenZipBatchesAsync` and `AcknowledgeZipBatchAsync` entirely (their responsibility moves to `IBackgroundJobRunService.GetOpenRunsAsync`/`AcknowledgeAsync`, already covered generically by `BackgroundJobRunServiceTests.cs` from PR #285). Delete this whole block:

```csharp
    public async Task<List<ZipBatchStatus>> GetOpenZipBatchesAsync() =>
        await db.ZipImportBatches
            .Where(b => !b.Acknowledged)
            .Select(b => new ZipBatchStatus
            {
                Id = b.Id,
                FileName = b.FileName,
                TotalEntries = b.TotalEntries,
                ProcessedEntries = b.ProcessedEntries,
                ImportedCount = b.ImportedCount,
                SkippedCount = b.SkippedCount,
                SkippedReasonsJson = b.SkippedReasonsJson,
                Done = b.Done,
                CreatedAt = b.CreatedAt,
            })
            .ToListAsync().ConfigureAwait(false);

    public async Task AcknowledgeZipBatchAsync(int batchId)
    {
        var batch = await db.ZipImportBatches.FirstOrDefaultAsync(b => b.Id == batchId).ConfigureAwait(false);
        if (batch is null) return;
        batch.Acknowledged = true;
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
```

Replace `UploadZipAsync`'s body (the part from the `entryCount > ZipMaxEntries` check onward — the size/entry-count checks above it are unchanged) — replace:

```csharp
            if (entryCount > ZipMaxEntries)
                return (null, $"För många filer i zip-filen (max {ZipMaxEntries}).");

            var strategy = db.Database.CreateExecutionStrategy();
            var batchId = await strategy.ExecuteAsync(async () =>
            {
                // A retry re-runs this whole delegate: a prior failed attempt may
                // have left a ZipImportBatch row tracked (Added) without committing
                // — detach it before adding a fresh one, or SaveChangesAsync would
                // insert both and produce a duplicate row.
                foreach (var stale in db.ChangeTracker.Entries<ZipImportBatch>().Where(e => e.State == EntityState.Added).ToList())
                    stale.State = EntityState.Detached;

#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tempReadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
#pragma warning restore CA2007
                var (oid, _) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, tempReadStream).ConfigureAwait(false);

                var batch = new ZipImportBatch
                {
                    OrganisationId = currentUser.OrganisationId.Value,
                    FileName = fileName,
                    StagingOid = oid,
                    TotalEntries = entryCount,
                };
                db.ZipImportBatches.Add(batch);
                await db.SaveChangesAsync().ConfigureAwait(false);

                await tx.CommitAsync().ConfigureAwait(false);
                return batch.Id;
            }).ConfigureAwait(false);

            zipImportQueue.Enqueue(batchId);

            return (batchId, null);
```

with:

```csharp
            if (entryCount > ZipMaxEntries)
                return (null, $"För många filer i zip-filen (max {ZipMaxEntries}).");

            var strategy = db.Database.CreateExecutionStrategy();
            var (runId, stagingOid) = await strategy.ExecuteAsync(async () =>
            {
                // A retry re-runs this whole delegate: a prior failed attempt may have
                // left a BackgroundJobRun row tracked (Added) without committing —
                // detach it before adding a fresh one, or SaveChangesAsync would insert
                // both and produce a duplicate row.
                foreach (var stale in db.ChangeTracker.Entries<BackgroundJobRun>().Where(e => e.State == EntityState.Added).ToList())
                    stale.State = EntityState.Detached;

#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tempReadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
#pragma warning restore CA2007
                var (oid, _) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, tempReadStream).ConfigureAwait(false);

                var run = await backgroundJobRunService.CreateRunAsync(BackgroundJobType.ZipImport, entryCount).ConfigureAwait(false);

                await tx.CommitAsync().ConfigureAwait(false);
                return (run.Id, oid);
            }).ConfigureAwait(false);

            zipImportQueue.Enqueue(runId, fileName, stagingOid);

            return (runId, null);
```

Note `CreateRunAsync` participates in the already-open `tx` because it saves through the same `db`/connection — same pattern the old code relied on for `db.ZipImportBatches.Add`. Add `using KoalaBooks.Domain.Entities;` to this file's usings if `BackgroundJobRun` isn't already resolvable (check — `KoalaBooks.Domain.Entities` is already imported at the top of the file for `Document`, so no new using is needed).

Finally, delete the now-unused `ZipBatchStatus` class at the bottom of the file:

```csharp
public class ZipBatchStatus
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public int TotalEntries { get; set; }
    public int ProcessedEntries { get; set; }
    public int ImportedCount { get; set; }
    public int SkippedCount { get; set; }
    public string SkippedReasonsJson { get; set; } = "[]";
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<SkippedEntry> SkippedReasons =>
        System.Text.Json.JsonSerializer.Deserialize<List<SkippedEntry>>(SkippedReasonsJson) ?? [];
}
```

- [ ] **Step 6: Update `IDocumentService.cs`**

In `src/KoalaBooks.Application/Services/IDocumentService.cs`, replace:

```csharp
    Task<(int? BatchId, string? Error)> UploadZipAsync(string fileName, Func<Stream> openZipData);
    Task<List<ZipBatchStatus>> GetOpenZipBatchesAsync();
    Task AcknowledgeZipBatchAsync(int batchId);
```

with:

```csharp
    Task<(int? RunId, string? Error)> UploadZipAsync(string fileName, Func<Stream> openZipData);
```

- [ ] **Step 7: Rewrite `ZipImportJobTests.cs`**

Replace the full contents of `tests/KoalaBooks.Tests/ZipImportJobTests.cs`:

```csharp
using System.IO.Compression;
using System.Text.Json;
using KoalaBooks.Application.Jobs;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace KoalaBooks.Tests;

public class ZipImportJobTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private async Task<(int RunId, uint StagingOid)> StageZipAsync(byte[] zipBytes, int entryCount)
    {
        uint oid;
        await using (var tx = await _fx.Db.Database.BeginTransactionAsync())
        {
            var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();
            (oid, _) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, new MemoryStream(zipBytes));
            await tx.CommitAsync();
        }

        var run = await _fx.MakeBackgroundJobRunService().CreateRunAsync(BackgroundJobType.ZipImport, entryCount);
        return (run.Id, oid);
    }

    private ZipImportJob MakeJob() =>
        new ZipImportJob(_fx.Options, new DbDocumentStorage(_fx.Db), new NoOpDocumentExtractionQueue(),
            new NoOpZipImportQueue(), NullLogger<ZipImportJob>.Instance);

    private async Task<BackgroundJobRun> ReloadRunAsync(int runId) =>
        await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().AsNoTracking().FirstAsync(r => r.Id == runId);

    private static ZipImportResult ParseResult(BackgroundJobRun run) =>
        JsonSerializer.Deserialize<ZipImportResult>(run.ResultJson!)!;

    [Fact]
    public async Task RunAsync_ImportsAllValidEntries()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1, 2, 3 }), ("b.png", new byte[] { 4, 5 }));
        var (runId, stagingOid) = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var run = await ReloadRunAsync(runId);
        Assert.Equal(BackgroundJobStatus.Completed, run.Status);
        Assert.Equal(2, run.ProcessedCount);
        var result = ParseResult(run);
        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(0, result.SkippedCount);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Equal(2, docs.Count);
        Assert.Contains(docs, d => d.FileName == "a.pdf");
        Assert.Contains(docs, d => d.FileName == "b.png");
    }

    [Fact]
    public async Task RunAsync_FlattensNestedFolderPaths()
    {
        var zip = BuildZip(("invoices/2026/faktura.pdf", new byte[] { 1, 2, 3 }));
        var (runId, stagingOid) = await StageZipAsync(zip, 1);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs);
        Assert.Equal("faktura.pdf", docs[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsDirectoryEntries()
    {
        var zip = BuildZipWithDirectoryEntry();
        var (runId, stagingOid) = await StageZipAsync(zip, 1);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs);
        Assert.Equal("faktura.pdf", docs[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsInvalidEntryType_ReportsReason()
    {
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.exe", new byte[] { 1, 2, 3 }));
        var (runId, stagingOid) = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var result = ParseResult(await ReloadRunAsync(runId));
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Single(result.SkippedReasons);
        Assert.Equal("bad.exe", result.SkippedReasons[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsOversizedEntry()
    {
        var bigData = new byte[11 * 1024 * 1024];
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("big.pdf", bigData));
        var (runId, stagingOid) = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var result = ParseResult(await ReloadRunAsync(runId));
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public async Task RunAsync_CorruptZipContainer_CompletesImmediately_NoEntriesProcessed()
    {
        var corruptBytes = new byte[] { 1, 2, 3, 4, 5 };
        var (runId, stagingOid) = await StageZipAsync(corruptBytes, 0);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var run = await ReloadRunAsync(runId);
        Assert.Equal(BackgroundJobStatus.Completed, run.Status);
        Assert.Equal(0, run.ProcessedCount);
        var result = ParseResult(run);
        Assert.Single(result.SkippedReasons);
    }

    [Fact]
    public async Task RunAsync_SkipsCorruptEntry_RestOfBatchStillImports()
    {
        var zip = CorruptEntryData(BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.pdf", new byte[500])), "bad.pdf");
        var (runId, stagingOid) = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var result = ParseResult(await ReloadRunAsync(runId));
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public async Task RunAsync_ResumesFromProcessedEntries_DoesNotReimportOnRetry()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1 }), ("b.pdf", new byte[] { 2 }), ("c.pdf", new byte[] { 3 }));
        var (runId, stagingOid) = await StageZipAsync(zip, 3);

        // Simulate a first attempt that processed the first entry then crashed (e.g. a
        // transient storage failure) before the job's process could move past it — the
        // run is left Pending (LoadRunAsync never got the chance to flip it to Running
        // and record a ClaimedByJobId), with ProcessedCount/ResultJson already reflecting
        // that one entry, exactly what SaveProgressAsync/Run.ResultJson would have
        // persisted mid-loop.
        var svc = _fx.MakeDocumentService();
        await svc.UploadAsync("a.pdf", "application/pdf", () => new MemoryStream(new byte[] { 1 }));
        var run = await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == runId);
        run.ProcessedCount = 1;
        run.ResultJson = JsonSerializer.Serialize(new ZipImportResult("test.zip", 1, 0, []));
        await _fx.Db.SaveChangesAsync();

        // Retry: RunAsync should resume from entry index 1, not reprocess "a.pdf", and the
        // final ImportedCount must include the entry the simulated first attempt already
        // imported.
        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var finalRun = await ReloadRunAsync(runId);
        Assert.Equal(BackgroundJobStatus.Completed, finalRun.Status);
        Assert.Equal(3, finalRun.ProcessedCount);
        var result = ParseResult(finalRun);
        Assert.Equal(3, result.ImportedCount);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs, d => d.FileName == "a.pdf"); // exactly one, not duplicated
        Assert.Single(docs, d => d.FileName == "b.pdf");
        Assert.Single(docs, d => d.FileName == "c.pdf");
    }

    [Fact]
    public async Task RunAsync_UnknownRunId_NoOpsWithoutThrowing()
    {
        await MakeJob().RunAsync(999_999, "test.zip", 0);
    }

    [Fact]
    public async Task RunAsync_AlreadyCompletedRun_NoOpsWithoutThrowing()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1 }));
        var (runId, stagingOid) = await StageZipAsync(zip, 1);
        var run = await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == runId);
        run.Status = BackgroundJobStatus.Completed;
        await _fx.Db.SaveChangesAsync();

        await MakeJob().RunAsync(runId, "test.zip", stagingOid); // must not throw even though stagingOid still points at a valid LO

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Empty(docs); // confirms it didn't reprocess
    }

    private static byte[] BuildZip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                entryStream.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    private static byte[] BuildZipWithDirectoryEntry()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("empty_folder/");
            var entry = archive.CreateEntry("faktura.pdf");
            using var entryStream = entry.Open();
            var data = new byte[] { 1, 2, 3 };
            entryStream.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    private static byte[] CorruptEntryData(byte[] zipBytes, string entryName)
    {
        var corrupted = (byte[])zipBytes.Clone();
        for (var i = 0; i < corrupted.Length - 4; i++)
        {
            if (corrupted[i] == 0x50 && corrupted[i + 1] == 0x4B && corrupted[i + 2] == 0x03 && corrupted[i + 3] == 0x04)
            {
                var nameLen = BitConverter.ToUInt16(corrupted, i + 26);
                var extraLen = BitConverter.ToUInt16(corrupted, i + 28);
                var nameStart = i + 30;
                var name = System.Text.Encoding.UTF8.GetString(corrupted, nameStart, nameLen);
                if (name == entryName)
                {
                    var compressedSize = BitConverter.ToInt32(corrupted, i + 18);
                    var dataStart = nameStart + nameLen + extraLen;
                    for (var j = dataStart; j < dataStart + compressedSize; j++)
                        corrupted[j] = (byte)~corrupted[j];
                    return corrupted;
                }
            }
        }
        throw new InvalidOperationException($"entry {entryName} not found in zip for corruption");
    }
}
```

- [ ] **Step 8: Rewrite `ZipImportJobRetryStrategyTests.cs`**

Replace the full contents of `tests/KoalaBooks.Tests/ZipImportJobRetryStrategyTests.cs`:

```csharp
using System.Text.Json;
using KoalaBooks.Application.Jobs;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace KoalaBooks.Tests;

public class ZipImportJobRetryStrategyTests : IDisposable
{
    private readonly string _dbName;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    // Only used to seed the org, stage the zip, and re-read the run afterwards —
    // ZipImportJob builds its own AppDbContext from _dbOptions internally, the same way
    // it does in production (see ZipImportJob.RunAsync's comment on why).
    private readonly AppDbContext _db;
    private readonly int _organisationId;

    public ZipImportJobRetryStrategyTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr, o => o.EnableRetryOnFailure())
            .Options;

        _db = new AppDbContext(_dbOptions, new LocalCurrentUser());
        _db.Database.EnsureCreated();

        var org = new Organisation { Name = "Test Org", Slug = "test-org" };
        _db.Organisations.Add(org);
        _db.SaveChanges();
        _organisationId = org.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    [Fact]
    public async Task RunAsync_ProcessesStagedRun_UnderRetryingExecutionStrategy()
    {
        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("a.pdf");
            using var entryStream = entry.Open();
            entryStream.Write([1, 2, 3]);
        }

        uint stagingOid;
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
            (stagingOid, _) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, new MemoryStream(ms.ToArray()));
            await tx.CommitAsync();
        }

        var runService = new BackgroundJobRunService(_db, new LocalCurrentUser(_organisationId));
        var run = await runService.CreateRunAsync(BackgroundJobType.ZipImport, totalCount: 1);

        var job = new ZipImportJob(_dbOptions, new DbDocumentStorage(_db), new NoOpDocumentExtractionQueue(),
            new NoOpZipImportQueue(), NullLogger<ZipImportJob>.Instance);

        await job.RunAsync(run.Id, "test.zip", stagingOid);

        // AsNoTracking: run is already tracked from the setup above, so a tracking query
        // would return the identity-mapped in-memory instance rather than re-reading the
        // actual persisted row — masking a failed SaveChangesAsync that never reached
        // Postgres.
        var updated = await _db.BackgroundJobRuns.IgnoreQueryFilters().AsNoTracking().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(BackgroundJobStatus.Completed, updated.Status);
        var result = JsonSerializer.Deserialize<ZipImportResult>(updated.ResultJson!)!;
        Assert.Equal(1, result.ImportedCount);
    }
}
```

- [ ] **Step 9: Update `DocumentServiceTests.cs`'s zip section**

In `tests/KoalaBooks.Tests/DocumentServiceTests.cs`, replace the `RecordingZipImportQueue` class at the bottom of the file:

```csharp
file class RecordingZipImportQueue : IZipImportQueue
{
    public List<int> EnqueuedBatchIds { get; } = [];
    public void Enqueue(int batchId) => EnqueuedBatchIds.Add(batchId);
}
```

with:

```csharp
file class RecordingZipImportQueue : IZipImportQueue
{
    public List<int> EnqueuedRunIds { get; } = [];
    public void Enqueue(int runId, string fileName, uint stagingOid) => EnqueuedRunIds.Add(runId);
}
```

Replace the whole zip test block (from `UploadZipAsync_ValidZip_CreatesBatchAndEnqueuesJob` through `GetOpenZipBatchesAsync_DeserializesSkippedReasons`, i.e. lines 385–537 as currently laid out) with:

```csharp
    [Fact]
    public async Task UploadZipAsync_ValidZip_CreatesRunAndEnqueuesJob()
    {
        var queue = new RecordingZipImportQueue();
        var svc = _fx.MakeDocumentService(queue);
        var zip = BuildZip(("a.pdf", new byte[] { 1, 2, 3 }), ("b.png", new byte[] { 4, 5 }));

        var (runId, err) = await svc.UploadZipAsync("test.zip", () => new MemoryStream(zip));

        Assert.Null(err);
        Assert.NotNull(runId);
        Assert.Single(queue.EnqueuedRunIds);
        Assert.Equal(runId, queue.EnqueuedRunIds[0]);

        var run = await _fx.Db.BackgroundJobRuns.FirstAsync(r => r.Id == runId);
        Assert.Equal(BackgroundJobType.ZipImport, run.JobType);
        Assert.Equal(BackgroundJobStatus.Pending, run.Status);
        Assert.Equal(2, run.TotalCount);
        Assert.Equal(0, run.ProcessedCount);
        Assert.False(run.Acknowledged);
    }

    [Fact]
    public async Task UploadZipAsync_RejectsOversizedZipContainer_NoStagingOrRunCreated()
    {
        var queue = new RecordingZipImportQueue();
        var svc = _fx.MakeDocumentService(queue);
        var bigZip = new byte[501 * 1024 * 1024];

        var (runId, err) = await svc.UploadZipAsync("test.zip", () => new MemoryStream(bigZip));

        Assert.Null(runId);
        Assert.NotNull(err);
        Assert.Empty(queue.EnqueuedRunIds);
        Assert.Empty(await _fx.Db.BackgroundJobRuns.Where(r => r.JobType == BackgroundJobType.ZipImport).ToListAsync());
    }

    [Fact]
    public async Task UploadZipAsync_RejectsZipWithTooManyEntries_NoStagingOrRunCreated()
    {
        var queue = new RecordingZipImportQueue();
        var svc = _fx.MakeDocumentService(queue);
        var entries = Enumerable.Range(1, 501)
            .Select(i => ($"file{i}.pdf", new byte[] { 1 }))
            .ToArray();
        var zip = BuildZip(entries);

        var (runId, err) = await svc.UploadZipAsync("test.zip", () => new MemoryStream(zip));

        Assert.Null(runId);
        Assert.NotNull(err);
        Assert.Empty(queue.EnqueuedRunIds);
        Assert.Empty(await _fx.Db.BackgroundJobRuns.Where(r => r.JobType == BackgroundJobType.ZipImport).ToListAsync());
    }

    [Fact]
    public async Task UploadZipAsync_AcceptsZipAtTheBoundary_500Entries()
    {
        var queue = new RecordingZipImportQueue();
        var svc = _fx.MakeDocumentService(queue);
        var entries = Enumerable.Range(1, 500)
            .Select(i => ($"file{i}.pdf", new byte[] { 1 }))
            .ToArray();
        var zip = BuildZip(entries);

        var (runId, err) = await svc.UploadZipAsync("test.zip", () => new MemoryStream(zip));

        Assert.Null(err);
        Assert.NotNull(runId);
        var run = await _fx.Db.BackgroundJobRuns.FirstAsync(r => r.Id == runId);
        Assert.Equal(500, run.TotalCount);
    }

    [Fact]
    public async Task UploadZipAsync_RejectsCorruptZipFile()
    {
        var queue = new RecordingZipImportQueue();
        var svc = _fx.MakeDocumentService(queue);
        var corruptBytes = new byte[] { 1, 2, 3, 4, 5 };

        var (runId, err) = await svc.UploadZipAsync("test.zip", () => new MemoryStream(corruptBytes));

        Assert.Null(runId);
        Assert.NotNull(err);
        Assert.Empty(queue.EnqueuedRunIds);
    }
```

(The deleted tests — `UploadZipAsync_PersistsFileName_ReturnedByGetOpenZipBatchesAsync`, `GetOpenZipBatchesAsync_ReturnsUnacknowledgedBatches_ExcludesAcknowledged`, `GetOpenZipBatchesAsync_IncludesDoneButUnacknowledgedBatches`, `GetOpenZipBatchesAsync_DeserializesSkippedReasons` — tested `DocumentService`-owned batch-status querying/acknowledgement that no longer exists: that responsibility is now `IBackgroundJobRunService.GetOpenRunsAsync`/`AcknowledgeAsync`, already covered generically by `BackgroundJobRunServiceTests.cs`'s `GetOpenRunsAsync_ReturnsOnlyUnacknowledgedRunsOfMatchingType` and related tests from PR #285. FileName is no longer queryable mid-run — it was never rendered anywhere for an in-progress batch in `Inbox.razor` either, only in the completion toast, which `ZipImportJobTests.cs`'s `ResultJson`-based assertions and Task 2's `InboxZipImportToastTests.cs` rewrite still cover.)

- [ ] **Step 9b: Fix `DocumentServiceTests.cs`'s `collidingSvc` call site (gap found during execution — not in the original Step 9 scope)**

`DocumentServiceTests.cs` has a third direct `new DocumentService(...)` call site, in the xmin-collision test around line 183, that Step 9 above didn't cover. Replace:

```csharp
        await using var collidingDb = new AppDbContext(options, TestFixture.MakeTenant(_fx.OrganisationId));
        var collidingSvc = new DocumentService(
            collidingDb, new DbDocumentStorage(collidingDb), new NoOpDocumentExtractionQueue(),
            new NoOpZipImportQueue(), TestFixture.MakeTenant(_fx.OrganisationId));
```

with:

```csharp
        await using var collidingDb = new AppDbContext(options, TestFixture.MakeTenant(_fx.OrganisationId));
        var collidingTenant = TestFixture.MakeTenant(_fx.OrganisationId);
        var collidingSvc = new DocumentService(
            collidingDb, new DbDocumentStorage(collidingDb), new NoOpDocumentExtractionQueue(),
            new NoOpZipImportQueue(), new BackgroundJobRunService(collidingDb, collidingTenant), collidingTenant);
```

- [ ] **Step 10: Update `DocumentServiceZipRetryStrategyTests.cs`**

Replace the full contents of `tests/KoalaBooks.Tests/DocumentServiceZipRetryStrategyTests.cs`:

```csharp
using KoalaBooks.Application.Jobs;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class DocumentServiceZipRetryStrategyTests : IDisposable
{
    private readonly string _dbName;
    private readonly AppDbContext _db;
    private readonly int _organisationId;

    public DocumentServiceZipRetryStrategyTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        // Mirrors Program.cs's EnrichNpgsqlDbContext, which enables a
        // retrying execution strategy in the real app — this is what
        // UploadZipAsync's manual staging transaction must be compatible with.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr, o => o.EnableRetryOnFailure())
            .Options;

        _db = new AppDbContext(options, new LocalCurrentUser());
        _db.Database.EnsureCreated();

        var org = new Organisation { Name = "Test Org", Slug = "test-org" };
        _db.Organisations.Add(org);
        _db.SaveChanges();
        _organisationId = org.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    [Fact]
    public async Task UploadZipAsync_StagesZip_UnderRetryingExecutionStrategy()
    {
        var currentUser = new LocalCurrentUser(_organisationId);
        var queue = new RecordingZipImportQueue();
        var svc = new DocumentService(_db, new DbDocumentStorage(_db),
            new NoOpDocumentExtractionQueue(), queue,
            new BackgroundJobRunService(_db, currentUser), currentUser);

        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("a.pdf");
            using var entryStream = entry.Open();
            entryStream.Write([1, 2, 3]);
        }
        var zipBytes = ms.ToArray();

        var (runId, err) = await svc.UploadZipAsync("test.zip", () => new MemoryStream(zipBytes));

        Assert.Null(err);
        Assert.NotNull(runId);

        // Staging succeeded iff the queue was handed a real (non-zero) large-object oid —
        // BackgroundJobRun itself has no StagingOid column to assert against directly
        // (see ZipImportJob's doc comment on why staging data flows through job args
        // instead of a persisted column).
        Assert.Single(queue.EnqueuedRunIds);
        Assert.NotEqual(0u, queue.EnqueuedStagingOid);

        // _db's currentUser has no active org, so bypass the tenant query filter.
        var run = await _db.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == runId);
        Assert.Equal(BackgroundJobType.ZipImport, run.JobType);
    }
}

file class RecordingZipImportQueue : IZipImportQueue
{
    public List<int> EnqueuedRunIds { get; } = [];
    public uint EnqueuedStagingOid { get; private set; }
    public void Enqueue(int runId, string fileName, uint stagingOid)
    {
        EnqueuedRunIds.Add(runId);
        EnqueuedStagingOid = stagingOid;
    }
}
```

- [ ] **Step 11: Update `DocumentExtractionJobTests.cs`'s two `new DocumentService(...)` call sites**

In `tests/KoalaBooks.Tests/DocumentExtractionJobTests.cs`, both `ConcurrentClassifyExtractor.ExtractAsync` and `ConcurrentDeleteExtractor.ExtractAsync` construct a `DocumentService` directly. Replace both occurrences of:

```csharp
        var concurrentSvc = new DocumentService(
            concurrentDb, new DbDocumentStorage(concurrentDb), new NoOpDocumentExtractionQueue(),
            new NoOpZipImportQueue(), TestFixture.MakeTenant(organisationId));
```

with:

```csharp
        var concurrentTenant = TestFixture.MakeTenant(organisationId);
        var concurrentSvc = new DocumentService(
            concurrentDb, new DbDocumentStorage(concurrentDb), new NoOpDocumentExtractionQueue(),
            new NoOpZipImportQueue(), new BackgroundJobRunService(concurrentDb, concurrentTenant), concurrentTenant);
```

(`ICurrentUser` returned by `TestFixture.MakeTenant` is used twice here — once for `DocumentService`, once for `BackgroundJobRunService` — so it's hoisted into a local rather than calling `MakeTenant` twice, which would construct two distinct `LocalCurrentUser` instances; either works since both just wrap the same `organisationId`, but reusing one is clearer intent.) Add `using KoalaBooks.Application.Services;` to this file's usings if `BackgroundJobRunService` isn't already resolvable — check first; `KoalaBooks.Application.Services` is likely already imported since `DocumentService` itself is used in this file.

- [ ] **Step 12: Build and run the affected test projects**

```bash
dotnet build KoalaBooks.slnx
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj --filter "FullyQualifiedName~ZipImportJob|FullyQualifiedName~DocumentServiceTests|FullyQualifiedName~DocumentServiceZipRetryStrategyTests|FullyQualifiedName~DocumentExtractionJobTests|FullyQualifiedName~BackgroundJobRunServiceTests|FullyQualifiedName~BackgroundJobRunBaseTests|FullyQualifiedName~BackgroundJobRunFailureFilterTests"
```

Expected: `Build succeeded.` and all filtered tests pass. (Full-suite verification, including `KoalaBooks.ComponentTests`, happens in Task 4 once Task 2's `Inbox.razor` changes land — `InboxZipImportToastTests.cs` in its current form will still be broken until then.)

- [ ] **Step 13: Commit**

```bash
git add src/KoalaBooks.Domain/Interfaces/IZipImportQueue.cs \
  src/KoalaBooks.Application/Jobs/HangfireZipImportQueue.cs \
  src/KoalaBooks.Application/Jobs/NoOpZipImportQueue.cs \
  src/KoalaBooks.Application/Jobs/ZipImportJob.cs \
  src/KoalaBooks.Application/Services/DocumentService.cs \
  src/KoalaBooks.Application/Services/IDocumentService.cs \
  tests/KoalaBooks.Tests/TestFixture.cs \
  tests/KoalaBooks.Tests/ZipImportJobTests.cs \
  tests/KoalaBooks.Tests/ZipImportJobRetryStrategyTests.cs \
  tests/KoalaBooks.Tests/DocumentServiceTests.cs \
  tests/KoalaBooks.Tests/DocumentServiceZipRetryStrategyTests.cs \
  tests/KoalaBooks.Tests/DocumentExtractionJobTests.cs
git commit -m "$(cat <<'EOF'
Retrofit ZipImportJob onto BackgroundJobRun/BackgroundJobRunBase

ZipImportBatch is replaced by a BackgroundJobRun(JobType.ZipImport) row.
Zip-specific input (file name, staging large-object oid) flows through as
ordinary Hangfire job arguments instead of a persisted column; output
(imported/skipped counts, skip reasons) flows through ResultJson, which
doubles as retry-safe interim progress so a resumed retry doesn't lose the
tally from entries an earlier attempt already processed.
EOF
)"
```

---

## Task 2: Retrofit `Inbox.razor` onto `BackgroundJobStatusPoller`

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Inbox.razor`
- Modify: `tests/KoalaBooks.ComponentTests/InboxZipImportToastTests.cs`

**Interfaces:**
- Consumes: `<BackgroundJobStatusPoller JobType="BackgroundJobType" StaleAfter="TimeSpan" PollInterval="TimeSpan" OnRunCompleted="EventCallback<BackgroundJobRun>" />` (`src/KoalaBooks.Components/Shared/BackgroundJobStatusPoller.razor`, from PR #285) and `ZipImportResult`'s JSON shape from Task 1 (`{FileName, ImportedCount, SkippedCount, SkippedReasons: [{FileName, Reason}]}`), read by property-name matching only — no compile-time reference to `KoalaBooks.Application.Jobs.ZipImportResult` from Components.

- [ ] **Step 1: Update `Inbox.razor`'s markup**

In `src/KoalaBooks.Components/Pages/Inbox.razor`, add the poller component right after the `<h1>` (it renders no markup itself, so placement only matters for where it sits in the component tree). Replace:

```razor
<h1>📥 Inkorg</h1>

@if (_error is not null)
```

with:

```razor
<h1>📥 Inkorg</h1>

<BackgroundJobStatusPoller JobType="BackgroundJobType.ZipImport"
                            StaleAfter="ZipImportStaleAfter"
                            OnRunCompleted="HandleZipImportCompletedAsync" />

@if (_error is not null)
```

Add `@using KoalaBooks.Components.Shared` and `@using KoalaBooks.Domain.Entities` to the top `@using` block (needed for `BackgroundJobStatusPoller` and the `BackgroundJobRun` parameter type in the callback) — the file already has `@using KoalaBooks.Domain.Enums` for `BackgroundJobType`. Replace:

```razor
@page "/inbox"
@implements IDisposable
@using KoalaBooks.Application.Services
@using KoalaBooks.Domain.Enums
@using MudBlazor
@using Microsoft.AspNetCore.Components.Forms
```

with:

```razor
@page "/inbox"
@implements IDisposable
@using KoalaBooks.Application.Services
@using KoalaBooks.Components.Shared
@using KoalaBooks.Domain.Entities
@using KoalaBooks.Domain.Enums
@using MudBlazor
@using Microsoft.AspNetCore.Components.Forms
@using System.Text.Json
```

- [ ] **Step 2: Remove the hand-rolled zip-batch polling code from `@code`**

Remove the `_openZipBatches` field and `ZipBatchStaleAfter` constant. Replace:

```csharp
    private List<DocumentMeta> _docs = [];
    private List<ZipBatchStatus> _openZipBatches = [];
    private bool _isLoading;
```

with:

```csharp
    private List<DocumentMeta> _docs = [];
    private bool _isLoading;
```

Replace:

```csharp
    // A zip batch does much more work than a single document (up to 500 entries), so it
    // gets a longer staleness allowance before the UI gives up watching an abandoned or
    // permanently-failed background job.
    private static readonly TimeSpan ZipBatchStaleAfter = TimeSpan.FromMinutes(30);
```

with:

```csharp
    // A zip import does much more work than a single document (up to 500 entries), so it
    // gets a longer staleness allowance before the poller gives up watching an abandoned
    // or permanently-failed background job.
    private static readonly TimeSpan ZipImportStaleAfter = TimeSpan.FromMinutes(30);
```

Remove the call to `RefreshZipBatchesAsync` from `LoadPageAsync`, and delete `RefreshZipBatchesAsync` itself. Replace:

```csharp
    private async Task LoadPageAsync(bool showSpinner = true)
    {
        if (showSpinner) _isLoading = true;
        var skip = (_page - 1) * PageSize;
        _docs = await DocumentService.GetPendingAsync(_filter, skip, PageSize, _sortBy, _sortAsc);
        _totalCount = await DocumentService.GetPendingCountAsync(_filter);
        await RefreshZipBatchesAsync();
        _isLoading = false;
        UpdatePolling();
    }

    private async Task RefreshZipBatchesAsync()
    {
        _openZipBatches = await DocumentService.GetOpenZipBatchesAsync();
        foreach (var batch in _openZipBatches.Where(b => b.Done).ToList())
        {
            var summary = $"{batch.FileName}: {batch.ImportedCount} dokument importerades";
            if (batch.SkippedCount > 0)
            {
                var reasons = string.Join(", ", batch.SkippedReasons.Select(s => $"{s.FileName}: {s.Reason}"));
                summary += $", {batch.SkippedCount} hoppade över ({reasons})";
            }
            // The default 3s auto-dismiss (Program.cs's SnackbarConfiguration) is too short here:
            // this toast only fires on the poll tick after the job finishes (up to 5s late), so a
            // user not staring at the screen at that exact moment would otherwise never see the
            // one place the imported/skipped counts are ever reported. RequireInteraction keeps it
            // up until dismissed.
            Snackbar.Add(summary, batch.SkippedCount > 0 ? Severity.Warning : Severity.Success,
                config => config.RequireInteraction = true);
            await DocumentService.AcknowledgeZipBatchAsync(batch.Id);
            _openZipBatches.Remove(batch);
        }
    }
```

with:

```csharp
    private async Task LoadPageAsync(bool showSpinner = true)
    {
        if (showSpinner) _isLoading = true;
        var skip = (_page - 1) * PageSize;
        _docs = await DocumentService.GetPendingAsync(_filter, skip, PageSize, _sortBy, _sortAsc);
        _totalCount = await DocumentService.GetPendingCountAsync(_filter);
        _isLoading = false;
        UpdatePolling();
    }

    private async Task HandleZipImportCompletedAsync(BackgroundJobRun run)
    {
        var result = JsonSerializer.Deserialize<ZipImportSummary>(run.ResultJson!)!;
        var summary = $"{result.FileName}: {result.ImportedCount} dokument importerades";
        if (result.SkippedCount > 0)
        {
            var reasons = string.Join(", ", result.SkippedReasons.Select(s => $"{s.FileName}: {s.Reason}"));
            summary += $", {result.SkippedCount} hoppade över ({reasons})";
        }
        // The default 3s auto-dismiss (Program.cs's SnackbarConfiguration) is too short here:
        // this toast only fires on the poll tick after the job finishes (up to 5s late), so a
        // user not staring at the screen at that exact moment would otherwise never see the
        // one place the imported/skipped counts are ever reported. RequireInteraction keeps it
        // up until dismissed.
        Snackbar.Add(summary, result.SkippedCount > 0 ? Severity.Warning : Severity.Success,
            config => config.RequireInteraction = true);
        await LoadPageAsync(); // Newly-imported docs won't show up until the doc list is refreshed.
    }

    // Deserialized from BackgroundJobRun.ResultJson by property-name matching only — see
    // KoalaBooks.Application.Jobs.ZipImportJob.ZipImportResult, which is the shape that
    // actually writes it. No compile-time reference between Components and Application.Jobs.
    private record ZipImportSummary(string FileName, int ImportedCount, int SkippedCount, List<SkippedEntryDto> SkippedReasons);
    private record SkippedEntryDto(string FileName, string Reason);
```

- [ ] **Step 3: Remove the now-unused `hasOpenBatches` staleness check from `UpdatePolling`**

Replace:

```csharp
    private void UpdatePolling()
    {
        var hasPendingDocs = _docs.Any(d =>
            d.ExtractionStatus == ExtractionStatus.Pending &&
            DateTime.UtcNow - d.UploadedAt < PendingStaleAfter);
        var hasOpenBatches = _openZipBatches.Any(b =>
            DateTime.UtcNow - b.CreatedAt < ZipBatchStaleAfter);
        if (hasPendingDocs || hasOpenBatches)
        {
            _pollTimer ??= new System.Threading.Timer(OnPollTick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        else
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }
```

with:

```csharp
    private void UpdatePolling()
    {
        var hasPendingDocs = _docs.Any(d =>
            d.ExtractionStatus == ExtractionStatus.Pending &&
            DateTime.UtcNow - d.UploadedAt < PendingStaleAfter);
        if (hasPendingDocs)
        {
            _pollTimer ??= new System.Threading.Timer(OnPollTick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        else
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }
```

(`Inbox.razor`'s own `_pollTimer` now exists purely for `Document.ExtractionStatus` polling — `<BackgroundJobStatusPoller>` owns its own independent timer for zip-import status, per the design doc's §5.)

- [ ] **Step 4: Update `UploadAsync`'s local variable naming for clarity (optional but matches Task 1's `RunId` rename)**

Replace:

```csharp
                if (isZip)
                {
                    var (batchId, zipErr) = await DocumentService.UploadZipAsync(file.Name, () => file.OpenReadStream(fileMaxBytes));
                    if (zipErr is not null)
```

with:

```csharp
                if (isZip)
                {
                    var (_, zipErr) = await DocumentService.UploadZipAsync(file.Name, () => file.OpenReadStream(fileMaxBytes));
                    if (zipErr is not null)
```

(The returned run id was never actually used after this point in the original code either — `Inbox.razor` never held onto `batchId`.)

- [ ] **Step 5: Rewrite `InboxZipImportToastTests.cs`**

Replace the full contents of `tests/KoalaBooks.ComponentTests/InboxZipImportToastTests.cs`:

```csharp
using System.Text.Json;
using KoalaBooks.Application.Services;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Reproduces #251: the toast shown when a background zip import finishes must name
// the zip file and report the imported count as "xx dokument importerades" - before
// this it only said "Import klar: xx importerade" with no way to tell which upload
// (if several were in flight) the toast referred to.
public class InboxZipImportToastTests : BunitContext
{
    private readonly IDocumentService _documentService = Substitute.For<IDocumentService>();
    private readonly IBackgroundJobRunService _backgroundJobRunService = Substitute.For<IBackgroundJobRunService>();

    public InboxZipImportToastTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_documentService);
        Services.AddSingleton(_backgroundJobRunService);
        Services.AddSingleton(Substitute.For<KoalaBooks.Application.Services.IDocumentProvider>());
        Services.AddSingleton(Substitute.For<ILogger<KoalaBooks.Components.Shared.BackgroundJobStatusPoller>>());

        _documentService.GetPendingAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns([]);
        _documentService.GetPendingCountAsync(Arg.Any<string?>()).Returns(0);
    }

    [Fact]
    public async Task FinishedZipImportRun_ShowsToastNamingTheZipFileAndImportedCount()
    {
        var resultJson = JsonSerializer.Serialize(new
        {
            FileName = "fakturor.zip",
            ImportedCount = 3,
            SkippedCount = 0,
            SkippedReasons = Array.Empty<object>()
        });
        _backgroundJobRunService.GetOpenRunsAsync(BackgroundJobType.ZipImport).Returns([
            new BackgroundJobRun
            {
                Id = 1,
                JobType = BackgroundJobType.ZipImport,
                Status = BackgroundJobStatus.Completed,
                ResultJson = resultJson,
                CreatedAt = DateTime.UtcNow
            }
        ]);

        var snackbarProvider = Render<MudSnackbarProvider>();
        await snackbarProvider.InvokeAsync(() => Render<Inbox>());

        snackbarProvider.WaitForAssertion(() =>
            Assert.Contains("fakturor.zip: 3 dokument importerades", snackbarProvider.Markup));
        _ = _backgroundJobRunService.Received(1).AcknowledgeAsync(1);
    }
}
```

- [ ] **Step 6: Build and run the component tests**

```bash
dotnet build KoalaBooks.slnx
dotnet test tests/KoalaBooks.ComponentTests/KoalaBooks.ComponentTests.csproj --filter "FullyQualifiedName~Inbox"
```

Expected: `Build succeeded.` and `InboxZipImportToastTests` passes.

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Inbox.razor tests/KoalaBooks.ComponentTests/InboxZipImportToastTests.cs
git commit -m "$(cat <<'EOF'
Retrofit Inbox.razor's zip-batch polling onto BackgroundJobStatusPoller

Replaces the hand-rolled zip-batch poll-timer/staleness/acknowledge block
with the shared BackgroundJobStatusPoller component from PR #285. The
completion toast now deserializes BackgroundJobRun.ResultJson by
property-name matching (no compile-time reference to
KoalaBooks.Application.Jobs from Components), matching the design doc's
"read by the page that knows how to render it" intent.
EOF
)"
```

---

## Task 3: Remove `ZipImportBatch` and generate the drop-table migration

**Files:**
- Delete: `src/KoalaBooks.Domain/Entities/ZipImportBatch.cs`
- Modify: `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`
- Create: `src/KoalaBooks.Infrastructure/Migrations/<timestamp>_RemoveZipImportBatch.cs` (and matching `.Designer.cs`), generated by `dotnet ef migrations add`
- Modify: `src/KoalaBooks.Infrastructure/Migrations/AppDbContextModelSnapshot.cs` (auto-updated by the same command)

By this point (after Tasks 1–2), nothing in `src/` or `tests/` references `ZipImportBatch` anymore — confirm that before starting this task.

- [ ] **Step 1: Confirm no remaining references**

```bash
grep -rn "ZipImportBatch" --include=*.cs --include=*.razor src/ tests/
```

Expected: no output. If anything shows up, Task 1 or Task 2 missed a spot — go back and fix it before continuing.

- [ ] **Step 2: Delete the entity and its model configuration**

Delete `src/KoalaBooks.Domain/Entities/ZipImportBatch.cs`.

In `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`, remove the `DbSet`:

```csharp
    public DbSet<BackgroundJobRun> BackgroundJobRuns => Set<BackgroundJobRun>();
    public DbSet<ZipImportBatch> ZipImportBatches => Set<ZipImportBatch>();
```

becomes:

```csharp
    public DbSet<BackgroundJobRun> BackgroundJobRuns => Set<BackgroundJobRun>();
```

and remove the model-builder block:

```csharp
        modelBuilder.Entity<ZipImportBatch>(entity =>
        {
            entity.Property(b => b.StagingOid).HasColumnType("oid");
            entity.HasQueryFilter(b => _currentUser.OrganisationId != null && b.OrganisationId == _currentUser.OrganisationId);
            entity.HasOne<Organisation>()
                  .WithMany()
                  .HasForeignKey(b => b.OrganisationId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(b => b.OrganisationId);
        });

```

(the blank line after it stays, matching the file's existing spacing between entity blocks — delete only the `modelBuilder.Entity<ZipImportBatch>(...)` block itself).

- [ ] **Step 3: Generate the migration**

```bash
dotnet ef migrations add RemoveZipImportBatch \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: new files appear under `src/KoalaBooks.Infrastructure/Migrations/`, and `AppDbContextModelSnapshot.cs` is updated. The generated `Up()` should contain a `DropTable("ZipImportBatches")` and the matching `DropIndex` calls — no hand-editing needed. This is a separate migration on top of PR #251's own `AddZipImportBatch` migration (rather than deleting/rewriting that already-pushed migration file) so `dotnet ef database update` stays a straight-line, idempotent history for any environment that may have already applied it.

- [ ] **Step 4: Build to confirm it compiles**

```bash
dotnet build KoalaBooks.slnx
```

Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Domain/Entities/ZipImportBatch.cs \
  src/KoalaBooks.Infrastructure/Data/AppDbContext.cs \
  src/KoalaBooks.Infrastructure/Migrations/
git commit -m "$(cat <<'EOF'
Remove ZipImportBatch entity, replaced by BackgroundJobRun

Nothing references ZipImportBatch after the ZipImportJob/DocumentService/
Inbox.razor retrofit. A separate drop-table migration is added on top of
PR #251's own AddZipImportBatch migration (rather than rewriting that
already-pushed migration) so any environment that already applied it keeps
a straight-line, idempotent history.
EOF
)"
```

---

## Task 4: Full-suite verification

**Files:** none (verification only).

- [ ] **Step 1: Run the full test suite**

```bash
dotnet test KoalaBooks.slnx
```

Expected: all tests pass (493 `KoalaBooks.Tests` + 22 `KoalaBooks.ComponentTests` from PR #285's baseline, plus/minus whatever Tasks 1–2 added or removed — no failures, no skips beyond any pre-existing ones).

- [ ] **Step 2: Confirm the migration applies cleanly on boot**

```bash
aspire start --isolated
```

Watch the KoalaBooks.Web resource logs for a clean startup (migrations apply, no unhandled exception) — this is the same check PR #285 used to confirm `AddBackgroundJobRuns` applied cleanly. Stop the app once confirmed (`aspire stop` or Ctrl-C per the `aspire` skill's usual flow).

- [ ] **Step 3: Manually exercise a real zip upload end-to-end**

Using the running Aspire app (from Step 2, or a fresh `aspire start --isolated`) and Playwright or a manual browser session against `/inbox`:
1. Upload a `.zip` containing at least one valid PDF/PNG/JPEG and one disallowed file type (e.g. a `.exe`).
2. Confirm the immediate "zip accepterad, bearbetas i bakgrunden…" toast appears.
3. Within ~5–10s, confirm the completion toast appears naming the zip file, the imported count, and the skip reason for the disallowed file — and that it stays up (doesn't auto-dismiss in 3s).
4. Confirm the imported document(s) now appear in the inbox list without a manual page reload.
5. Check the Hangfire dashboard (`/hangfire`) to confirm the `ZipImportJob` run shows as `Succeeded`.

This exercises the exact path PR #251's own original manual verification covered — confirming the retrofit didn't regress it.

- [x] **Step 4: Report results**

**Manual verification found and fixed two real regressions the automated test suite could not catch** (mocked services / bUnit's synchronous rendering never exercise a real, concurrently-accessed EF Core `DbContext`):

1. `Inbox.razor` crashed on load with `System.InvalidOperationException: A second operation was started on this context instance before a previous operation completed.` `BackgroundJobStatusPoller`'s `OnInitializedAsync` (added by Task 2) raced against `Inbox.razor`'s own `OnInitializedAsync` (`LoadPageAsync`) — both touch the same Blazor-circuit-scoped `AppDbContext`, and EF Core disallows concurrent operations on one instance. This is the first time `BackgroundJobStatusPoller` (PR #285) has been hosted on a page that also does its own `OnInitializedAsync` DB work, so the hazard was never triggered before. Fixed by giving `BackgroundJobRunService.GetOpenRunsAsync`/`AcknowledgeAsync` (the poller's only DB access) their own short-lived `AppDbContext` per call — `CreateRunAsync` still uses the ambient one, since `DocumentService.UploadZipAsync` needs it to participate in its own transaction. Matches Microsoft's documented Blazor+EF Core guidance ("use one context per operation"); verified via Context7 that a naive `AddDbContextFactory` singleton factory would have re-used the *first-resolved* `ICurrentUser` for every subsequent call instead of the correct per-circuit one, so the fix constructs `AppDbContext` directly from `DbContextOptions<AppDbContext>` + the already-correctly-scoped `ICurrentUser`, not via `IDbContextFactory`.
2. A second, narrower instance of the same root cause: `Inbox.razor`'s own `OnRunCompleted` callback (`HandleZipImportCompletedAsync`) called `await LoadPageAsync()` to refresh the doc list — but that callback can fire from *inside* `BackgroundJobStatusPoller.OnInitializedAsync`, racing against `Inbox.razor`'s own concurrent `OnInitializedAsync` the same way. Fixed by dropping that call entirely — newly-imported documents are created with `ExtractionStatus.Pending`, which already keeps `Inbox.razor`'s own poll timer running, so they surface on its next tick (≤5s) without an explicit refresh.

Both fixes were verified end-to-end via Playwright against a real running app (`aspire start --isolated`): uploaded a zip with 2 valid PDFs + 1 disallowed file — both PDFs imported (confirmed in the doc list), the disallowed file skipped, no `ErrorBoundary`/unhandled-exception log lines across two separate uploads and a page reload (both of which reproduced crash #1/#2 before the fix). Full suite re-run after the fixes: 530/530 passing (506 `KoalaBooks.Tests` + 24 `KoalaBooks.ComponentTests`).

Commit for this task's fixes (unlike the rest of Task 4, which is verification-only): `BackgroundJobRunService.cs`, `Inbox.razor`, and the mechanical `new BackgroundJobRunService(...)` call-site updates in tests.
