# Background-process zip inbox imports (#207) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move zip inbox uploads off the synchronous request path onto a Hangfire background job, stream the container instead of buffering it, and raise the per-zip limits to 500 entries / 500MB.

**Architecture:** `DocumentService.UploadZipAsync` validates and stages the zip (spooled through a local temp file, never fully held in memory) into a Postgres Large Object, creates a `ZipImportBatch` row, and enqueues a `ZipImportJob`. The job copies the staged LO to its own local temp file, opens it as a `ZipArchive`, and uploads each entry through the existing `DocumentService.UploadAsync` — reusing the extraction pipeline #208 already built. `Inbox.razor`'s existing poll-timer is extended to also watch open batches and show one summary toast when a batch finishes.

**Tech Stack:** ASP.NET Core / Blazor Server, EF Core + Npgsql (PostgreSQL), Hangfire (already wired for `DocumentExtractionJob`), xUnit + Testcontainers for tests.

## Global Constraints

- **Blocked on #236** ("stream uploads through to storage") merging to `main` first — this plan is written against the post-#236 signatures `DocumentService.UploadAsync(string fileName, string contentType, Func<Stream> openData)` and `IDocumentStorage.SaveAsync(int documentId, string contentType, Func<Stream> openData)`. Do not start Task 1 until #236 is on `main`.
- New limits: `ZipMaxEntries = 500`, `ZipMaxBytes = 500 * 1024 * 1024` (500MB). Per-file 10MB cap is unchanged and enforced automatically by `UploadAsync`'s existing `MaxBytesEnforcingStream`.
- No custom `Seek`-capable Postgres-backed `Stream` — `NpgsqlLargeObjectManager`/`NpgsqlLargeObjectStream` are `[Obsolete]` since Npgsql 8.0 (confirmed against the installed `Npgsql 10.0.3` package: both carry `[Obsolete("... call these yourself directly")]`). All Postgres Large Object access goes through plain SQL `lo_*` function calls, exactly like `DbDocumentStorage` already does.
- No parallel/fan-out processing of zip entries — one `ZipImportJob` processes a batch's entries sequentially.
- No batch-level progress bar — individual documents reveal one by one via the existing per-document `Pending → Completed` display; the batch only drives one final summary toast.

---

## Task 1: Extract shared Postgres Large Object copy helpers

**Files:**
- Create: `src/KoalaBooks.Infrastructure/Services/PostgresLargeObjects.cs`
- Modify: `src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs`
- Test: `tests/KoalaBooks.Tests/PostgresLargeObjectsTests.cs`

**Interfaces:**
- Produces: `PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(NpgsqlConnection conn, Stream source) -> Task<(uint Oid, long Length)>`, `PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(NpgsqlConnection conn, uint oid, Stream destination) -> Task`, `PostgresLargeObjects.DeleteLargeObjectAsync(NpgsqlConnection conn, uint oid) -> Task` — used directly by Task 4 (staging write) and Task 5 (job's read-to-temp-file and cleanup). The caller is responsible for opening/committing the surrounding `NpgsqlTransaction` (these helpers assume one is already active, matching how `DbDocumentStorage` already manages its own transactions).

This is a pure extraction: `DbDocumentStorage`'s existing behavior (its retry-strategy wrapping, transaction boundaries, `DocumentData` row handling) is unchanged — only its internal chunk-loop bodies move into the new shared class.

- [ ] **Step 1: Write the failing test for the new shared helpers**

```csharp
// tests/KoalaBooks.Tests/PostgresLargeObjectsTests.cs
using KoalaBooks.Infrastructure.Services;
using Npgsql;

namespace KoalaBooks.Tests;

public class PostgresLargeObjectsTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task CopyStreamIntoNewLargeObjectAsync_ThenCopyLargeObjectIntoStreamAsync_RoundTripsBytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 250, 251, 252 };
        await using var tx = await _fx.Db.Database.BeginTransactionAsync();
        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();

        var (oid, length) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, new MemoryStream(bytes));
        Assert.Equal(bytes.Length, length);

        using var readBack = new MemoryStream();
        await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, oid, readBack);
        Assert.Equal(bytes, readBack.ToArray());

        await PostgresLargeObjects.DeleteLargeObjectAsync(conn, oid);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task CopyStreamIntoNewLargeObjectAsync_HandlesEmptyStream()
    {
        await using var tx = await _fx.Db.Database.BeginTransactionAsync();
        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();

        var (oid, length) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, new MemoryStream());
        Assert.Equal(0, length);

        using var readBack = new MemoryStream();
        await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, oid, readBack);
        Assert.Empty(readBack.ToArray());

        await tx.CommitAsync();
    }

    [Fact]
    public async Task CopyStreamIntoNewLargeObjectAsync_HandlesDataLargerThanChunkSize()
    {
        var bytes = new byte[200_000]; // larger than the 80KB chunk size used internally
        new Random(42).NextBytes(bytes);
        await using var tx = await _fx.Db.Database.BeginTransactionAsync();
        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();

        var (oid, length) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, new MemoryStream(bytes));
        Assert.Equal(bytes.Length, length);

        using var readBack = new MemoryStream();
        await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, oid, readBack);
        Assert.Equal(bytes, readBack.ToArray());

        await tx.CommitAsync();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/KoalaBooks.Tests --filter PostgresLargeObjectsTests`
Expected: FAIL to compile — `PostgresLargeObjects` doesn't exist yet.

- [ ] **Step 3: Create the shared helper class**

```csharp
// src/KoalaBooks.Infrastructure/Services/PostgresLargeObjects.cs
using Npgsql;
using NpgsqlTypes;

namespace KoalaBooks.Infrastructure.Services;

// Plain-SQL lo_* function calls, matching DbDocumentStorage's existing approach —
// deliberately not Npgsql's NpgsqlLargeObjectManager/NpgsqlLargeObjectStream, which
// are [Obsolete] as of Npgsql 8.0 specifically in favor of calling these functions
// directly. Callers own the surrounding transaction; these are sequential-only
// (no Seek) — that's sufficient for both current callers, which each need either a
// forward write or a forward read, never random access.
public static class PostgresLargeObjects
{
    // https://www.postgresql.org/docs/current/lo-interfaces.html#LO-INTERFACES-OPEN
    private const int InvWrite = 0x00020000;
    private const int InvRead = 0x00040000;
    private const int ChunkSize = 81920; // matches Stream.CopyToAsync's default buffer size

    public static async Task<(uint Oid, long Length)> CopyStreamIntoNewLargeObjectAsync(NpgsqlConnection conn, Stream source)
    {
        var oid = await ExecuteScalarAsync<uint>(conn, "SELECT lo_create(0)");
        var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
            ("oid", NpgsqlDbType.Oid, oid), ("mode", NpgsqlDbType.Integer, InvWrite));

        var buffer = new byte[ChunkSize];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            length += read;
            var chunk = buffer[..read];
            await ExecuteScalarAsync<int>(conn, "SELECT lowrite(@fd, @chunk)",
                ("fd", NpgsqlDbType.Integer, fd), ("chunk", NpgsqlDbType.Bytea, chunk));
        }
        await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd));

        return (oid, length);
    }

    public static async Task CopyLargeObjectIntoStreamAsync(NpgsqlConnection conn, uint oid, Stream destination)
    {
        var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
            ("oid", NpgsqlDbType.Oid, oid), ("mode", NpgsqlDbType.Integer, InvRead));

        while (true)
        {
            var chunk = await ExecuteScalarAsync<byte[]>(conn, "SELECT loread(@fd, @len)",
                ("fd", NpgsqlDbType.Integer, fd), ("len", NpgsqlDbType.Integer, ChunkSize));
            if (chunk.Length > 0) await destination.WriteAsync(chunk);
            if (chunk.Length < ChunkSize) break;
        }
        await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd));
    }

    public static Task DeleteLargeObjectAsync(NpgsqlConnection conn, uint oid) =>
        ExecuteScalarAsync<int>(conn, "SELECT lo_unlink(@oid)", ("oid", NpgsqlDbType.Oid, oid));

    private static async Task<T> ExecuteScalarAsync<T>(NpgsqlConnection conn, string sql,
        params (string Name, NpgsqlDbType Type, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, type, value) in parameters)
            cmd.Parameters.Add(new NpgsqlParameter { ParameterName = name, NpgsqlDbType = type, Value = value });
        var result = await cmd.ExecuteScalarAsync();
        return (T)result!;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/KoalaBooks.Tests --filter PostgresLargeObjectsTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Refactor `DbDocumentStorage` to call the shared helpers**

Modify `src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs` — replace the inline chunk loops in `SaveAsync` and `LoadAsync` with calls to `PostgresLargeObjects`, keeping every other line (retry-strategy wrapping, transaction boundaries, `DocumentData` row handling, `DetachTrackedDocumentData`) unchanged:

```csharp
// src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace KoalaBooks.Infrastructure.Services;

public class DbDocumentStorage(AppDbContext db) : IDocumentStorage
{
    public async Task<(string StorageKey, long FileSize)> SaveAsync(int documentId, string contentType, Func<Stream> openData)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retry re-runs this whole delegate: a prior failed attempt may
            // have left a DocumentData row tracked (Added/Modified) without
            // committing — detach just that row before re-reading it. db is a
            // shared, caller-owned AppDbContext, so this must not touch
            // entities outside our own.
            DetachTrackedDocumentData(documentId);

            await using var data = openData();

            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var conn = (NpgsqlConnection)db.Database.GetDbConnection();

                var existing = await db.DocumentData.FindAsync(documentId);
                if (existing is not null)
                    await PostgresLargeObjects.DeleteLargeObjectAsync(conn, existing.Oid);

                var (oid, fileSize) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, data);

                if (existing is not null)
                    existing.Oid = oid;
                else
                    db.DocumentData.Add(new DocumentData { DocumentId = documentId, Oid = oid });

                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return (documentId.ToString(), fileSize);
            }
            catch
            {
                // A thrown exception leaves this attempt's tracked DocumentData behind
                // even though the DB rolled back — detach it so the caller's context
                // isn't left in an inconsistent state (this matters most when the
                // execution strategy has exhausted all retries and rethrows to the
                // caller, since no further attempt will run the start-of-attempt detach).
                DetachTrackedDocumentData(documentId);
                throw;
            }
        });
    }

    public async Task<byte[]> LoadAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return [];

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var row = await db.DocumentData.FindAsync(id);
            if (row is null) return [];

            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            using var ms = new MemoryStream();
            await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, row.Oid, ms);
            await tx.CommitAsync();
            return ms.ToArray();
        });
    }

    public async Task DeleteAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return;

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            DetachTrackedDocumentData(id);

            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var row = await db.DocumentData.FindAsync(id);
                if (row is null) return;

                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
                await PostgresLargeObjects.DeleteLargeObjectAsync(conn, row.Oid);
                db.DocumentData.Remove(row);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                // A thrown exception leaves this attempt's tracked DocumentData behind
                // even though the DB rolled back — detach it so the caller's context
                // isn't left in an inconsistent state (this matters most when the
                // execution strategy has exhausted all retries and rethrows to the
                // caller, since no further attempt will run the start-of-attempt detach).
                DetachTrackedDocumentData(id);
                throw;
            }
        });
    }

    // Detaches only a stale DocumentData entry left tracked by a previous,
    // retried attempt of this same call — never touches unrelated entities
    // tracked by the caller on this shared AppDbContext.
    private void DetachTrackedDocumentData(int documentId)
    {
        var entry = db.ChangeTracker.Entries<DocumentData>()
            .FirstOrDefault(e => e.Entity.DocumentId == documentId);
        if (entry is not null) entry.State = EntityState.Detached;
    }
}
```

- [ ] **Step 6: Run the full existing `DbDocumentStorage` test suite to confirm no regression**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~DbDocumentStorage"`
Expected: PASS — all pre-existing `DbDocumentStorageTests` and `DbDocumentStorageRetryStrategyTests` still pass unchanged, since `SaveAsync`/`LoadAsync`/`DeleteAsync`'s external behavior is identical.

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Services/PostgresLargeObjects.cs \
        src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs \
        tests/KoalaBooks.Tests/PostgresLargeObjectsTests.cs
git commit -m "refactor: extract shared Postgres large object copy helpers from DbDocumentStorage"
```

---

## Task 2: Add the `ZipImportBatch` entity, migration, and `AppDbContext` wiring

**Files:**
- Create: `src/KoalaBooks.Domain/Entities/ZipImportBatch.cs`
- Modify: `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`
- Create: `src/KoalaBooks.Infrastructure/Migrations/<timestamp>_AddZipImportBatch.cs` (generated)
- Create: `src/KoalaBooks.Infrastructure/Migrations/<timestamp>_AddZipImportBatch.Designer.cs` (generated, not hand-edited)
- Modify: `src/KoalaBooks.Infrastructure/Migrations/AppDbContextModelSnapshot.cs` (generated, not hand-edited)

**Interfaces:**
- Produces: `ZipImportBatch` entity with `Id`, `OrganisationId`, `StagingOid` (`uint?`), `TotalEntries`, `ProcessedEntries`, `ImportedCount`, `SkippedCount`, `SkippedReasonsJson` (`string`, default `"[]"`), `Done` (`bool`), `Acknowledged` (`bool`), `CreatedAt` (`DateTime`) — consumed by Task 4 (creates rows), Task 5 (`ZipImportJob` updates them), Task 6 (`DocumentService` queries them). `db.ZipImportBatches` DbSet on `AppDbContext`, org-scoped via query filter (same pattern as `Document`).

- [ ] **Step 1: Write the entity**

```csharp
// src/KoalaBooks.Domain/Entities/ZipImportBatch.cs
namespace KoalaBooks.Domain.Entities;

public class ZipImportBatch
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public uint? StagingOid { get; set; }
    public int TotalEntries { get; set; }
    public int ProcessedEntries { get; set; }
    public int ImportedCount { get; set; }
    public int SkippedCount { get; set; }
    public string SkippedReasonsJson { get; set; } = "[]";
    public bool Done { get; set; }
    public bool Acknowledged { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 2: Wire it into `AppDbContext`**

Modify `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs` — add the `DbSet` next to `Document`'s (line 53):

```csharp
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentData> DocumentData => Set<DocumentData>();
    public DbSet<ZipImportBatch> ZipImportBatches => Set<ZipImportBatch>();
```

Then add entity configuration right after the `DocumentData` block (after line 300, before the `BankTransaction` block):

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

- [ ] **Step 3: Generate the migration**

Run:
```bash
dotnet ef migrations add AddZipImportBatch \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: new files `src/KoalaBooks.Infrastructure/Migrations/<timestamp>_AddZipImportBatch.cs` and `.Designer.cs` appear, creating a `ZipImportBatches` table with all the properties above plus an FK to `Organisations` and an index on `OrganisationId`; `AppDbContextModelSnapshot.cs` is updated to match. This is a plain new-table addition — no data migration needed, the generated `Up`/`Down` should be used as-is.

- [ ] **Step 4: Apply the migration and verify the schema**

Run: `dotnet ef database update --project src/KoalaBooks.Infrastructure --startup-project src/KoalaBooks.Web`
Expected: migration applies cleanly against the local dev database with no errors.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Domain/Entities/ZipImportBatch.cs \
        src/KoalaBooks.Infrastructure/Data/AppDbContext.cs \
        src/KoalaBooks.Infrastructure/Migrations/
git commit -m "feat: add ZipImportBatch entity and migration"
```

---

## Task 3: Add `IZipImportQueue` and its Hangfire/no-op implementations

**Files:**
- Create: `src/KoalaBooks.Domain/Interfaces/IZipImportQueue.cs`
- Create: `src/KoalaBooks.Application/Jobs/HangfireZipImportQueue.cs`
- Create: `src/KoalaBooks.Application/Jobs/NoOpZipImportQueue.cs`
- Modify: `src/KoalaBooks.Web/Program.cs`
- Modify: `tests/KoalaBooks.Tests/TestFixture.cs`

**Interfaces:**
- Produces: `IZipImportQueue.Enqueue(int batchId)`, `HangfireZipImportQueue`, `NoOpZipImportQueue` — consumed by Task 4 (`DocumentService.UploadZipAsync` calls `Enqueue`) and by tests (a recording fake, added in Task 4, implements this same interface).
- Consumes: `ZipImportJob.RunAsync(int batchId)` (defined in Task 5) — `HangfireZipImportQueue` references it by name in `jobClient.Enqueue<ZipImportJob>(...)`. Since Task 5 comes after this task, `HangfireZipImportQueue` won't compile until Task 5 adds `ZipImportJob` — that's fine, this task's own step 4 registers it but the solution won't build end-to-end until Task 5 lands. Steps 1-3 below are self-contained and independently testable via the no-op path; step 4's build will be completed in Task 5.

This task mirrors `IDocumentExtractionQueue`/`HangfireDocumentExtractionQueue`/`NoOpDocumentExtractionQueue` exactly.

- [ ] **Step 1: Add the interface**

```csharp
// src/KoalaBooks.Domain/Interfaces/IZipImportQueue.cs
namespace KoalaBooks.Domain.Interfaces;

public interface IZipImportQueue
{
    void Enqueue(int batchId);
}
```

- [ ] **Step 2: Add the no-op implementation (used in tests and anywhere Hangfire isn't wired)**

```csharp
// src/KoalaBooks.Application/Jobs/NoOpZipImportQueue.cs
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class NoOpZipImportQueue : IZipImportQueue
{
    public void Enqueue(int batchId) { }
}
```

- [ ] **Step 3: Add the Hangfire-backed implementation**

```csharp
// src/KoalaBooks.Application/Jobs/HangfireZipImportQueue.cs
using Hangfire;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class HangfireZipImportQueue(IBackgroundJobClient jobClient) : IZipImportQueue
{
    public void Enqueue(int batchId) =>
        jobClient.Enqueue<ZipImportJob>(job => job.RunAsync(batchId));
}
```

Note: this won't compile until Task 5 adds `ZipImportJob` — that's expected, this task's build will only fully succeed once Task 5 is also done. If running tasks strictly in order with a build+test gate after each, skip the build-verification step for this task alone and verify compilation as part of Task 5 instead.

- [ ] **Step 4: Register both in `Program.cs`, mirroring the existing `IDocumentExtractionQueue` registration**

Modify `src/KoalaBooks.Web/Program.cs` (around line 61-68):

```csharp
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(koalabooksConnectionString)));
    builder.Services.AddHangfireServer();
    builder.Services.AddScoped<KoalaBooks.Application.Jobs.DocumentExtractionJob>();
    builder.Services.AddScoped<KoalaBooks.Domain.Interfaces.IDocumentExtractionQueue,
        KoalaBooks.Application.Jobs.HangfireDocumentExtractionQueue>();
    builder.Services.AddScoped<KoalaBooks.Application.Jobs.ZipImportJob>();
    builder.Services.AddScoped<KoalaBooks.Domain.Interfaces.IZipImportQueue,
        KoalaBooks.Application.Jobs.HangfireZipImportQueue>();
}
else
{
    builder.Services.AddScoped<KoalaBooks.Domain.Interfaces.IDocumentExtractionQueue,
        KoalaBooks.Application.Jobs.NoOpDocumentExtractionQueue>();
    builder.Services.AddScoped<KoalaBooks.Domain.Interfaces.IZipImportQueue,
        KoalaBooks.Application.Jobs.NoOpZipImportQueue>();
}
```

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Domain/Interfaces/IZipImportQueue.cs \
        src/KoalaBooks.Application/Jobs/HangfireZipImportQueue.cs \
        src/KoalaBooks.Application/Jobs/NoOpZipImportQueue.cs \
        src/KoalaBooks.Web/Program.cs
git commit -m "feat: add IZipImportQueue and Hangfire/no-op implementations"
```

(This commit will not build in isolation since `ZipImportJob` doesn't exist yet — that's expected and resolved by Task 5. If your workflow requires green builds per commit, squash Tasks 3-5 together instead of committing here.)

Note: `TestFixture.cs`'s `MakeDocumentService` overloads are **not** updated here even though they'll need to pass an `IZipImportQueue` — that change depends on `DocumentService`'s constructor gaining that parameter, which happens in Task 4. Updating `TestFixture.cs` here would reference a constructor parameter that doesn't exist yet. Task 4 Step 1 handles it instead.

---

## Task 4: Rewrite `DocumentService.UploadZipAsync` (staging, validation, batch creation, enqueue)

**Files:**
- Modify: `src/KoalaBooks.Application/Services/DocumentService.cs`
- Test: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Consumes: `PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync` (Task 1), `ZipImportBatch` (Task 2), `IZipImportQueue` (Task 3).
- Produces: `DocumentService.UploadZipAsync(Func<Stream> openZipData) -> Task<(int? BatchId, string? Error)>` — consumed by Task 7 (`Inbox.razor`). `DocumentService`'s constructor now takes an additional `IZipImportQueue zipImportQueue` parameter — `Program.cs`'s `AddScoped<DocumentService>()` needs no change since it resolves constructor params from DI automatically, but every test helper that constructs `DocumentService` directly needs updating (Step 1 below).

- [ ] **Step 1: Update `TestFixture.cs` for the new `DocumentService` constructor parameter**

Modify `tests/KoalaBooks.Tests/TestFixture.cs` — update the existing `MakeDocumentService` overloads (lines 188-195) to pass an `IZipImportQueue`, and add one new overload for tests that need to observe/record enqueues:

```csharp
    public DocumentService MakeDocumentService() =>
        MakeDocumentService(new DbDocumentStorage(Db));

    public DocumentService MakeDocumentService(IDocumentStorage storage) =>
        new DocumentService(Db, storage, new NoOpDocumentExtractionQueue(), new NoOpZipImportQueue(), _currentUser);

    public DocumentService MakeDocumentService(IDocumentExtractionQueue extractionQueue) =>
        new DocumentService(Db, new DbDocumentStorage(Db), extractionQueue, new NoOpZipImportQueue(), _currentUser);

    public DocumentService MakeDocumentService(IZipImportQueue zipImportQueue) =>
        new DocumentService(Db, new DbDocumentStorage(Db), new NoOpDocumentExtractionQueue(), zipImportQueue, _currentUser);
```

`using KoalaBooks.Application.Jobs;` is already present in `TestFixture.cs` (from the existing `NoOpDocumentExtractionQueue` usage), so `NoOpZipImportQueue` resolves without a new `using`.

This step alone doesn't add new test cases, so there's no separate red/green cycle — it's scaffolding consumed by Step 2's new tests below. Confirm it at least compiles: `dotnet build tests/KoalaBooks.Tests`.

- [ ] **Step 2: Write the failing tests for the new `UploadZipAsync`**

Replace the entire block of existing `UploadZipAsync_*` tests in `tests/KoalaBooks.Tests/DocumentServiceTests.cs` (currently lines 247-380, from `UploadZipAsync_ImportsAllValidEntries` through the `CorruptEntryData` helper) with:

```csharp
    [Fact]
    public async Task UploadZipAsync_ValidZip_CreatesBatchAndEnqueuesJob()
    {
        var queue = new RecordingZipImportQueue();
        var svc = _fx.MakeDocumentService(queue);
        var zip = BuildZip(("a.pdf", new byte[] { 1, 2, 3 }), ("b.png", new byte[] { 4, 5 }));

        var (batchId, err) = await svc.UploadZipAsync(() => new MemoryStream(zip));

        Assert.Null(err);
        Assert.NotNull(batchId);
        Assert.Single(queue.EnqueuedBatchIds);
        Assert.Equal(batchId, queue.EnqueuedBatchIds[0]);

        var batch = await _fx.Db.ZipImportBatches.FirstAsync(b => b.Id == batchId);
        Assert.Equal(2, batch.TotalEntries);
        Assert.Equal(0, batch.ProcessedEntries);
        Assert.False(batch.Done);
        Assert.False(batch.Acknowledged);
        Assert.NotNull(batch.StagingOid);
    }

    [Fact]
    public async Task UploadZipAsync_RejectsOversizedZipContainer_NoStagingOrBatchCreated()
    {
        var queue = new RecordingZipImportQueue();
        var svc = _fx.MakeDocumentService(queue);
        var bigZip = new byte[501 * 1024 * 1024];

        var (batchId, err) = await svc.UploadZipAsync(() => new MemoryStream(bigZip));

        Assert.Null(batchId);
        Assert.NotNull(err);
        Assert.Empty(queue.EnqueuedBatchIds);
        Assert.Empty(await _fx.Db.ZipImportBatches.ToListAsync());
    }

    [Fact]
    public async Task UploadZipAsync_RejectsZipWithTooManyEntries_NoStagingOrBatchCreated()
    {
        var queue = new RecordingZipImportQueue();
        var svc = _fx.MakeDocumentService(queue);
        var entries = Enumerable.Range(1, 501)
            .Select(i => ($"file{i}.pdf", new byte[] { 1 }))
            .ToArray();
        var zip = BuildZip(entries);

        var (batchId, err) = await svc.UploadZipAsync(() => new MemoryStream(zip));

        Assert.Null(batchId);
        Assert.NotNull(err);
        Assert.Empty(queue.EnqueuedBatchIds);
        Assert.Empty(await _fx.Db.ZipImportBatches.ToListAsync());
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

        var (batchId, err) = await svc.UploadZipAsync(() => new MemoryStream(zip));

        Assert.Null(err);
        Assert.NotNull(batchId);
        var batch = await _fx.Db.ZipImportBatches.FirstAsync(b => b.Id == batchId);
        Assert.Equal(500, batch.TotalEntries);
    }

    [Fact]
    public async Task UploadZipAsync_RejectsCorruptZipFile()
    {
        var queue = new RecordingZipImportQueue();
        var svc = _fx.MakeDocumentService(queue);
        var corruptBytes = new byte[] { 1, 2, 3, 4, 5 };

        var (batchId, err) = await svc.UploadZipAsync(() => new MemoryStream(corruptBytes));

        Assert.Null(batchId);
        Assert.NotNull(err);
        Assert.Empty(queue.EnqueuedBatchIds);
    }

    private static byte[] BuildZip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
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
```

Add `file class RecordingZipImportQueue : IZipImportQueue` at the bottom of `tests/KoalaBooks.Tests/DocumentServiceTests.cs`, next to the existing `file class FailingStorage`/`RecordingExtractionQueue`:

```csharp
file class RecordingZipImportQueue : IZipImportQueue
{
    public List<int> EnqueuedBatchIds { get; } = [];
    public void Enqueue(int batchId) => EnqueuedBatchIds.Add(batchId);
}
```

Update the pre-existing `file class FailingStorage : IDocumentStorage` to match the post-#236 signature (this should already have been done by #236 itself, but confirm it reads):

```csharp
file class FailingStorage : IDocumentStorage
{
    public Task<(string StorageKey, long FileSize)> SaveAsync(int documentId, string contentType, Func<Stream> openData) =>
        throw new InvalidOperationException("simulated storage failure");

    public Task<byte[]> LoadAsync(string storageKey) => Task.FromResult(Array.Empty<byte>());
    public Task DeleteAsync(string storageKey) => Task.CompletedTask;
}
```

Note: the removed tests (`UploadZipAsync_ImportsAllValidEntries`, `_FlattensNestedFolderPaths`, `_SkipsDirectoryEntries`, `_SkipsInvalidEntriesAndReportsReasons`, `_SkipsOversizedEntry`, `_SkipsEntryWhenStorageFails_RestOfBatchStillImports`, `_SkipsCorruptEntry_RestOfBatchStillImports`) tested *entry processing*, which now happens inside `ZipImportJob` instead of `UploadZipAsync` — equivalent coverage for those move to Task 5's `ZipImportJobTests`. Keep the `CorruptEntryData` helper and `BuildZipWithDirectoryEntry` helper (still used by Task 5's tests) — do not delete them even though no test in *this* file calls them anymore after this edit; if the compiler flags them unused, leave `BuildZip` and `CorruptEntryData` as `internal static` (not `private`) so Task 5's new `ZipImportJobTests.cs` file can reference them, or duplicate the small helpers there — duplicating is simpler and keeps each test file self-contained, so duplicate them in Task 5 instead of sharing.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter UploadZipAsync`
Expected: FAIL to compile — `UploadZipAsync` still has the old `byte[] zipData` signature and old return type.

- [ ] **Step 4: Rewrite `UploadZipAsync` and the `DocumentService` constructor**

Modify `src/KoalaBooks.Application/Services/DocumentService.cs`:

Change the constructor and add the `ZipMaxBytes`/`ZipMaxEntries` constants (already present, just confirm values updated) at the top of the class:

```csharp
public class DocumentService(
    AppDbContext db,
    IDocumentStorage storage,
    IDocumentExtractionQueue extractionQueue,
    IZipImportQueue zipImportQueue,
    ICurrentUser currentUser)
{
    private const long MaxBytes = 10 * 1024 * 1024;
    private const long ZipMaxBytes = 500 * 1024 * 1024;
    private const int ZipMaxEntries = 500;
```

Add `using KoalaBooks.Infrastructure.Services;` and `using Npgsql;` to the top of the file (needed for `PostgresLargeObjects` and the `NpgsqlConnection` cast).

Replace the entire `UploadZipAsync` method with the version below, and delete the now-fully-unused `ReadBoundedAsync` helper — the old `UploadZipAsync` was its only caller, and `ZipImportJob` (Task 5) never needs it either, since `UploadAsync`'s own `MaxBytesEnforcingStream` (from #236) already enforces the per-entry 10MB cap:

```csharp
    public async Task<(int? BatchId, string? Error)> UploadZipAsync(Func<Stream> openZipData)
    {
        if (currentUser.OrganisationId is null)
            return (null, "Ingen aktiv organisation.");

        var tempPath = Path.GetTempFileName();
        try
        {
            long totalBytes;
            await using (var tempWriteStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            await using (var source = openZipData())
            {
                var buffer = new byte[81920];
                totalBytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > ZipMaxBytes)
                    {
                        return (null, "Zip-filen är för stor (max 500 MB).");
                    }
                    await tempWriteStream.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            int entryCount;
            try
            {
                await using var tempReadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                using var archive = new ZipArchive(tempReadStream, ZipArchiveMode.Read);
                entryCount = archive.Entries.Count(e => !string.IsNullOrEmpty(e.Name));
            }
            catch (InvalidDataException)
            {
                return (null, "Ogiltig zip-fil.");
            }

            if (entryCount > ZipMaxEntries)
                return (null, $"För många filer i zip-filen (max {ZipMaxEntries}).");

            uint stagingOid;
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
                await using var tempReadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                (stagingOid, _) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, tempReadStream);
                await tx.CommitAsync();
            }

            var batch = new ZipImportBatch
            {
                OrganisationId = currentUser.OrganisationId.Value,
                StagingOid = stagingOid,
                TotalEntries = entryCount,
            };
            db.ZipImportBatches.Add(batch);
            await db.SaveChangesAsync();

            zipImportQueue.Enqueue(batch.Id);

            return (batch.Id, null);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
```

Note: `MaxBytesEnforcingStream` and `DocumentTooLargeException` (from #236) stay exactly as-is — they're used by `UploadAsync`, unrelated to this method.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "UploadZipAsync|PostgresLargeObjects"`
Expected: PASS (5 new `UploadZipAsync_*` tests, plus the 3 `PostgresLargeObjectsTests` still passing)

- [ ] **Step 6: Run the full `DocumentServiceTests` suite**

Run: `dotnet test tests/KoalaBooks.Tests --filter DocumentServiceTests`
Expected: PASS — every other existing `DocumentServiceTests` test (unrelated to zip upload) is unaffected.

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Application/Services/DocumentService.cs \
        tests/KoalaBooks.Tests/DocumentServiceTests.cs
git commit -m "feat: rewrite UploadZipAsync to stage the zip and enqueue background processing"
```

---

## Task 5: Add `ZipImportJob`

**Files:**
- Create: `src/KoalaBooks.Application/Jobs/ZipImportJob.cs`
- Test: `tests/KoalaBooks.Tests/ZipImportJobTests.cs`

**Interfaces:**
- Consumes: `ZipImportBatch` (Task 2), `PostgresLargeObjects` (Task 1), `DocumentService.UploadAsync(string, string, Func<Stream>)` (post-#236, existing).
- Produces: `ZipImportJob.RunAsync(int batchId)` — consumed by `HangfireZipImportQueue` (Task 3, completes its build).

This task completes the build started in Task 3 (`HangfireZipImportQueue` references `ZipImportJob.RunAsync` by name).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/KoalaBooks.Tests/ZipImportJobTests.cs
using System.IO.Compression;
using System.Text.Json;
using KoalaBooks.Application.Jobs;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace KoalaBooks.Tests;

public class ZipImportJobTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private async Task<int> StageZipAsync(byte[] zipBytes, int entryCount)
    {
        uint oid;
        await using (var tx = await _fx.Db.Database.BeginTransactionAsync())
        {
            var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();
            (oid, _) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, new MemoryStream(zipBytes));
            await tx.CommitAsync();
        }

        var batch = new ZipImportBatch
        {
            OrganisationId = _fx.OrganisationId,
            StagingOid = oid,
            TotalEntries = entryCount,
        };
        _fx.Db.ZipImportBatches.Add(batch);
        await _fx.Db.SaveChangesAsync();
        return batch.Id;
    }

    private ZipImportJob MakeJob() =>
        new ZipImportJob(_fx.Db, _fx.MakeDocumentService(), NullLogger<ZipImportJob>.Instance);

    [Fact]
    public async Task RunAsync_ImportsAllValidEntries()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1, 2, 3 }), ("b.png", new byte[] { 4, 5 }));
        var batchId = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(batchId);

        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        Assert.True(batch.Done);
        Assert.Equal(2, batch.ProcessedEntries);
        Assert.Equal(2, batch.ImportedCount);
        Assert.Equal(0, batch.SkippedCount);
        Assert.Null(batch.StagingOid);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Equal(2, docs.Count);
        Assert.Contains(docs, d => d.FileName == "a.pdf");
        Assert.Contains(docs, d => d.FileName == "b.png");
    }

    [Fact]
    public async Task RunAsync_FlattensNestedFolderPaths()
    {
        var zip = BuildZip(("invoices/2026/faktura.pdf", new byte[] { 1, 2, 3 }));
        var batchId = await StageZipAsync(zip, 1);

        await MakeJob().RunAsync(batchId);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs);
        Assert.Equal("faktura.pdf", docs[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsDirectoryEntries()
    {
        var zip = BuildZipWithDirectoryEntry();
        var batchId = await StageZipAsync(zip, 1);

        await MakeJob().RunAsync(batchId);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs);
        Assert.Equal("faktura.pdf", docs[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsInvalidEntryType_ReportsReason()
    {
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.exe", new byte[] { 1, 2, 3 }));
        var batchId = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(batchId);

        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        Assert.Equal(1, batch.ImportedCount);
        Assert.Equal(1, batch.SkippedCount);
        var skipped = JsonSerializer.Deserialize<List<SkippedEntry>>(batch.SkippedReasonsJson)!;
        Assert.Single(skipped);
        Assert.Equal("bad.exe", skipped[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsOversizedEntry()
    {
        var bigData = new byte[11 * 1024 * 1024];
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("big.pdf", bigData));
        var batchId = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(batchId);

        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        Assert.Equal(1, batch.ImportedCount);
        Assert.Equal(1, batch.SkippedCount);
    }

    [Fact]
    public async Task RunAsync_CorruptZipContainer_MarksDoneImmediately_NoEntriesProcessed()
    {
        var corruptBytes = new byte[] { 1, 2, 3, 4, 5 };
        var batchId = await StageZipAsync(corruptBytes, 0);

        await MakeJob().RunAsync(batchId);

        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        Assert.True(batch.Done);
        Assert.Equal(0, batch.ProcessedEntries);
        var skipped = JsonSerializer.Deserialize<List<SkippedEntry>>(batch.SkippedReasonsJson)!;
        Assert.Single(skipped);
    }

    [Fact]
    public async Task RunAsync_SkipsCorruptEntry_RestOfBatchStillImports()
    {
        var zip = CorruptEntryData(BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.pdf", new byte[500])), "bad.pdf");
        var batchId = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(batchId);

        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        Assert.Equal(1, batch.ImportedCount);
        Assert.Equal(1, batch.SkippedCount);
    }

    [Fact]
    public async Task RunAsync_ResumesFromProcessedEntries_DoesNotReimportOnRetry()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1 }), ("b.pdf", new byte[] { 2 }), ("c.pdf", new byte[] { 3 }));
        var batchId = await StageZipAsync(zip, 3);

        // Simulate a first attempt that processed the first entry then crashed
        // (e.g. a transient storage failure) before saving further progress.
        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        var svc = _fx.MakeDocumentService();
        await svc.UploadAsync("a.pdf", "application/pdf", () => new MemoryStream(new byte[] { 1 }));
        batch.ProcessedEntries = 1;
        batch.ImportedCount = 1;
        await _fx.Db.SaveChangesAsync();

        // Retry: RunAsync should resume from entry index 1, not reprocess "a.pdf".
        await MakeJob().RunAsync(batchId);

        var finalBatch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        Assert.True(finalBatch.Done);
        Assert.Equal(3, finalBatch.ProcessedEntries);
        Assert.Equal(3, finalBatch.ImportedCount);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs, d => d.FileName == "a.pdf"); // exactly one, not duplicated
        Assert.Single(docs, d => d.FileName == "b.pdf");
        Assert.Single(docs, d => d.FileName == "c.pdf");
    }

    [Fact]
    public async Task RunAsync_UnknownBatchId_NoOpsWithoutThrowing()
    {
        await MakeJob().RunAsync(999_999);
    }

    [Fact]
    public async Task RunAsync_AlreadyDoneBatch_NoOpsWithoutThrowing()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1 }));
        var batchId = await StageZipAsync(zip, 1);
        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        batch.Done = true;
        await _fx.Db.SaveChangesAsync();

        await MakeJob().RunAsync(batchId); // must not throw even though StagingOid still points at a valid LO

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

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter ZipImportJobTests`
Expected: FAIL to compile — `ZipImportJob` and `SkippedEntry` don't exist yet.

- [ ] **Step 3: Write `ZipImportJob`**

```csharp
// src/KoalaBooks.Application/Jobs/ZipImportJob.cs
using Hangfire;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.IO.Compression;
using System.Text.Json;

namespace KoalaBooks.Application.Jobs;

public record SkippedEntry(string FileName, string Reason);

public class ZipImportJob(AppDbContext db, DocumentService documentService, ILogger<ZipImportJob> logger)
{
    private static readonly Dictionary<string, string> ZipEntryContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };

    // IgnoreQueryFilters: this job has no HttpContext, so ICurrentUser.OrganisationId
    // is always null and the tenant query filter would hide the batch — same reasoning
    // as DocumentExtractionJob. Safe here because the job only acts on a batchId handed
    // to it by trusted code that just created that exact row.
    //
    // A batch left un-Done after all 3 retries are exhausted is not specially recovered
    // here — it simply stays Done=false forever, the same way DocumentExtractionJob
    // leaves a Document stuck at ExtractionStatus.Pending if its own retries run out.
    // Inbox.razor's poll-timer already has a staleness cutoff for exactly this class of
    // problem (see PendingStaleAfter) and gets an equivalent one for batches in Task 7.
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(int batchId)
    {
        var batch = await db.ZipImportBatches.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == batchId);
        if (batch is null || batch.Done) return;

        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite))
            {
                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    var conn = (NpgsqlConnection)db.Database.GetDbConnection();
                    await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, batch.StagingOid!.Value, tempStream);
                    await tx.CommitAsync();
                }
                tempStream.Position = 0;

                ZipArchive archive;
                try
                {
                    archive = new ZipArchive(tempStream, ZipArchiveMode.Read, leaveOpen: true);
                }
                catch (InvalidDataException)
                {
                    await AppendSkippedAsync(batch, "(zip-fil)", "Ogiltig zip-fil.");
                    batch.Done = true;
                    await db.SaveChangesAsync();
                    await DeleteStagingAsync(batch);
                    return;
                }

                using (archive)
                {
                    var fileEntries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

                    foreach (var entry in fileEntries.Skip(batch.ProcessedEntries))
                    {
                        if (!ZipEntryContentTypes.TryGetValue(Path.GetExtension(entry.Name), out var contentType))
                        {
                            await AppendSkippedAsync(batch, entry.Name, "Otillåten filtyp.");
                        }
                        else
                        {
                            try
                            {
                                var entryFullName = entry.FullName;
                                var (doc, err) = await documentService.UploadAsync(
                                    entry.Name, contentType, () => archive.GetEntry(entryFullName)!.Open());
                                if (doc is not null)
                                    batch.ImportedCount++;
                                else
                                    await AppendSkippedAsync(batch, entry.Name, err ?? "Okänt fel.");
                            }
                            catch (InvalidDataException)
                            {
                                await AppendSkippedAsync(batch, entry.Name, "Skadad fil.");
                            }
                        }

                        batch.ProcessedEntries++;
                        await db.SaveChangesAsync();
                    }
                }
            }

            batch.Done = true;
            await db.SaveChangesAsync();
            await DeleteStagingAsync(batch);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private async Task AppendSkippedAsync(ZipImportBatch batch, string fileName, string reason)
    {
        var skipped = JsonSerializer.Deserialize<List<SkippedEntry>>(batch.SkippedReasonsJson) ?? [];
        skipped.Add(new SkippedEntry(fileName, reason));
        batch.SkippedReasonsJson = JsonSerializer.Serialize(skipped);
        batch.SkippedCount++;
        await Task.CompletedTask;
    }

    private async Task DeleteStagingAsync(ZipImportBatch batch)
    {
        if (batch.StagingOid is null) return;

        await using var tx = await db.Database.BeginTransactionAsync();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        try
        {
            await PostgresLargeObjects.DeleteLargeObjectAsync(conn, batch.StagingOid.Value);
        }
        catch (Exception ex)
        {
            // The batch has already been marked Done and the local temp file cleaned up —
            // a leaked LO here is a minor storage-cleanup miss, not a correctness issue for
            // the batch itself, so log and move on rather than failing the whole run.
            logger.LogWarning(ex, "Failed to delete staging large object {Oid} for batch {BatchId}", batch.StagingOid, batch.Id);
            return;
        }
        batch.StagingOid = null;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter ZipImportJobTests`
Expected: PASS (10 tests)

- [ ] **Step 5: Run the full test suite to confirm no regression and that the solution builds end-to-end**

Run: `dotnet build && dotnet test tests/KoalaBooks.Tests`
Expected: build succeeds (this is the first point `HangfireZipImportQueue` from Task 3 actually compiles); all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Application/Jobs/ZipImportJob.cs \
        tests/KoalaBooks.Tests/ZipImportJobTests.cs
git commit -m "feat: add ZipImportJob for background zip entry processing"
```

---

## Task 6: Add `DocumentService.GetOpenZipBatchesAsync`/`AcknowledgeZipBatchAsync`

**Files:**
- Modify: `src/KoalaBooks.Application/Services/DocumentService.cs`
- Test: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Produces: `DocumentService.GetOpenZipBatchesAsync() -> Task<List<ZipBatchStatus>>`, `DocumentService.AcknowledgeZipBatchAsync(int batchId) -> Task`, and the `ZipBatchStatus` DTO (`Id`, `TotalEntries`, `ProcessedEntries`, `ImportedCount`, `SkippedCount`, `SkippedReasons` (`List<SkippedEntry>`), `Done`, `CreatedAt`) — consumed by Task 7 (`Inbox.razor`'s poll-timer).

- [ ] **Step 1: Write the failing tests**

Add `using KoalaBooks.Application.Jobs;` to the top of `tests/KoalaBooks.Tests/DocumentServiceTests.cs` (needed for `SkippedEntry`, referenced in the third test below), then add to the class body:

```csharp
    [Fact]
    public async Task GetOpenZipBatchesAsync_ReturnsUnacknowledgedBatches_ExcludesAcknowledged()
    {
        var svc = _fx.MakeDocumentService();
        var zip = BuildZip(("a.pdf", new byte[] { 1 }));
        var (batchId, _) = await svc.UploadZipAsync(() => new MemoryStream(zip));

        var open = await svc.GetOpenZipBatchesAsync();
        Assert.Single(open);
        Assert.Equal(batchId, open[0].Id);

        await svc.AcknowledgeZipBatchAsync(batchId!.Value);

        var afterAck = await svc.GetOpenZipBatchesAsync();
        Assert.Empty(afterAck);
    }

    [Fact]
    public async Task GetOpenZipBatchesAsync_IncludesDoneButUnacknowledgedBatches()
    {
        var svc = _fx.MakeDocumentService();
        var zip = BuildZip(("a.pdf", new byte[] { 1 }));
        var (batchId, _) = await svc.UploadZipAsync(() => new MemoryStream(zip));

        var batch = await _fx.Db.ZipImportBatches.FirstAsync(b => b.Id == batchId);
        batch.Done = true;
        batch.ImportedCount = 1;
        await _fx.Db.SaveChangesAsync();

        var open = await svc.GetOpenZipBatchesAsync();
        Assert.Single(open);
        Assert.True(open[0].Done);
        Assert.Equal(1, open[0].ImportedCount);
    }

    [Fact]
    public async Task GetOpenZipBatchesAsync_DeserializesSkippedReasons()
    {
        var svc = _fx.MakeDocumentService();
        var zip = BuildZip(("a.pdf", new byte[] { 1 }));
        var (batchId, _) = await svc.UploadZipAsync(() => new MemoryStream(zip));

        var batch = await _fx.Db.ZipImportBatches.FirstAsync(b => b.Id == batchId);
        batch.SkippedReasonsJson = System.Text.Json.JsonSerializer.Serialize(new[] { new SkippedEntry("bad.exe", "Otillåten filtyp.") });
        batch.SkippedCount = 1;
        await _fx.Db.SaveChangesAsync();

        var open = await svc.GetOpenZipBatchesAsync();
        Assert.Single(open[0].SkippedReasons);
        Assert.Equal("bad.exe", open[0].SkippedReasons[0].FileName);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter "GetOpenZipBatchesAsync"`
Expected: FAIL to compile — the methods don't exist yet.

- [ ] **Step 3: Add the methods and DTO**

Modify `src/KoalaBooks.Application/Services/DocumentService.cs` — add near the other query methods (after `GetPendingCountAsync`, before `PendingQuery`):

```csharp
    public async Task<List<ZipBatchStatus>> GetOpenZipBatchesAsync() =>
        await db.ZipImportBatches
            .Where(b => !b.Acknowledged)
            .Select(b => new ZipBatchStatus
            {
                Id = b.Id,
                TotalEntries = b.TotalEntries,
                ProcessedEntries = b.ProcessedEntries,
                ImportedCount = b.ImportedCount,
                SkippedCount = b.SkippedCount,
                SkippedReasonsJson = b.SkippedReasonsJson,
                Done = b.Done,
                CreatedAt = b.CreatedAt,
            })
            .ToListAsync();

    public async Task AcknowledgeZipBatchAsync(int batchId)
    {
        var batch = await db.ZipImportBatches.FirstOrDefaultAsync(b => b.Id == batchId);
        if (batch is null) return;
        batch.Acknowledged = true;
        await db.SaveChangesAsync();
    }
```

Add the `ZipBatchStatus` DTO next to `DocumentMeta` at the bottom of the file:

```csharp
public class ZipBatchStatus
{
    public int Id { get; set; }
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

Add `using KoalaBooks.Application.Jobs;` to the top of `DocumentService.cs` (for the `SkippedEntry` record defined in `ZipImportJob.cs`).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "GetOpenZipBatchesAsync"`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Application/Services/DocumentService.cs \
        tests/KoalaBooks.Tests/DocumentServiceTests.cs
git commit -m "feat: add DocumentService methods for polling and acknowledging zip import batches"
```

---

## Task 7: Wire `Inbox.razor` — new upload call, batch polling, summary toast

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Inbox.razor`

**Interfaces:**
- Consumes: `DocumentService.UploadZipAsync(Func<Stream>)`, `GetOpenZipBatchesAsync()`, `AcknowledgeZipBatchAsync(int)` (Tasks 4, 6).

- [ ] **Step 1: Update the zip branch of `UploadAsync` to call the new signature**

Modify `src/KoalaBooks.Components/Pages/Inbox.razor` — in the `@code` block's `UploadAsync` method, replace the `isZip` branch (currently reading the whole file into a `MemoryStream` and calling the old `UploadZipAsync(byte[])`):

```csharp
                if (isZip)
                {
                    var (batchId, zipErr) = await DocumentService.UploadZipAsync(() => file.OpenReadStream(fileMaxBytes));
                    if (zipErr is not null)
                    {
                        _error = $"{file.Name}: {zipErr}";
                    }
                    else
                    {
                        Snackbar.Add($"{file.Name}: zip accepterad, bearbetas i bakgrunden…", Severity.Info);
                    }
                    continue;
                }
```

Also update the `fileMaxBytes`/size constants and the help text above the upload button to reflect the new 500MB/500-entries limits:

```csharp
                const long maxBytes = 10 * 1024 * 1024;
                const long zipMaxBytes = 500 * 1024 * 1024;
```

And the static help text (currently `"...max 10 MB per fil (50 MB för ZIP)..."`):

```html
        Dra och släpp filer här eller klicka — PDF, PNG, JPG eller ZIP · max 10 MB per fil (500 MB för ZIP, upp till 500 dokument) · upp till 10 filer
```

- [ ] **Step 2: Extend polling to include open zip batches**

Add a field and extend `LoadPageAsync`/`UpdatePolling` to also track batches. Modify the `@code` block:

```csharp
    private List<DocumentMeta> _docs = [];
    private List<ZipBatchStatus> _openZipBatches = [];
    private bool _isLoading;
    private bool _uploading;
    private string? _error;
    private string _filter = "all";
    private string _sortBy = "uploadedAt";
    private bool _sortAsc = false;
    private int _page = 1;
    private int _totalCount;
    private const int PageSize = 50;
    private System.Threading.Timer? _pollTimer;
    private int _isPolling;
    private bool _disposed;

    // A doc pending this long has exhausted its storage-load retries without the job
    // ever syncing that failure back to ExtractionStatus — stop polling for it.
    private static readonly TimeSpan PendingStaleAfter = TimeSpan.FromMinutes(10);

    // A zip batch does much more work than a single document (up to 500 entries), so it
    // gets a longer staleness allowance before the UI gives up watching an abandoned or
    // permanently-failed background job.
    private static readonly TimeSpan ZipBatchStaleAfter = TimeSpan.FromMinutes(30);
```

Replace `LoadPageAsync` and `UpdatePolling`:

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
            var summary = $"Import klar: {batch.ImportedCount} importerade";
            if (batch.SkippedCount > 0)
            {
                var reasons = string.Join(", ", batch.SkippedReasons.Select(s => $"{s.FileName}: {s.Reason}"));
                summary += $", {batch.SkippedCount} hoppade över ({reasons})";
            }
            Snackbar.Add(summary, batch.SkippedCount > 0 ? Severity.Warning : Severity.Success);
            await DocumentService.AcknowledgeZipBatchAsync(batch.Id);
            _openZipBatches.Remove(batch);
        }
    }

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

- [ ] **Step 3: Build and manually verify end-to-end**

This is a Blazor UI change — verify by running the app (see the project's `run` skill / `aspire` skill if available) and, in a browser:
1. Upload a `.zip` containing a handful of valid PDFs/images and one invalid file (e.g. a `.exe`) — confirm a "zip accepterad, bearbetas i bakgrunden…" toast appears immediately and the upload button re-enables right away (not blocked for the whole batch).
2. Watch the inbox list — confirm the valid documents appear one by one within a few seconds (Hangfire picks up the job quickly in dev).
3. Confirm exactly one final summary toast appears once the batch finishes, correctly reporting imported/skipped counts and the skip reason.
4. Refresh the page mid-import (before the batch finishes) — confirm documents already imported are visible, and polling resumes and eventually shows the summary toast once the job completes.
5. Upload a zip with 501 entries or over 500MB — confirm it's rejected synchronously with an error message, not accepted.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Inbox.razor
git commit -m "feat: wire Inbox.razor to background zip import batches"
```

---

## Post-plan note

Once all 7 tasks are merged, revisit the issue's own suggestion to raise limits further if 500/500MB proves conservative in practice — this plan intentionally keeps that number fixed rather than making it configurable, per YAGNI; bumping it later is a one-line constant change plus a new migration is not needed (no schema depends on the literal values).
