# Remove Remaining Upload Buffering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove `DocumentService.UploadAsync`'s last full-file `byte[]` buffer so a single-file upload streams straight from the browser into a Postgres Large Object, with no full-file copy anywhere in the request.

**Architecture:** `IDocumentStorage.SaveAsync` moves from accepting an already-open `Stream` to a `Func<Stream> openData` factory, returning `(string StorageKey, long FileSize)` instead of just `string`. `DbDocumentStorage.SaveAsync` calls `openData()` fresh on every transient-failure-retry attempt instead of rewinding a seekable stream, which lets its `CanSeek`/non-seekable-throw guard be deleted outright. `DocumentService.UploadAsync` drops its upfront `ReadBoundedAsync` buffering and instead wraps the caller's factory in a new `MaxBytesEnforcingStream` decorator that enforces the 10 MB cap while streaming. All four upload call sites (`Inbox.razor`, `CustomerInvoices.razor`, `SupplierInvoices.razor`, `Journal.razor`) pass a factory lambda (`() => file.OpenReadStream(maxBytes)`) instead of an already-opened stream, and no longer own stream disposal — `DbDocumentStorage.SaveAsync` does, per attempt.

**Tech Stack:** .NET 10 / EF Core 10 / `Npgsql.EntityFrameworkCore.PostgreSQL` (raw `NpgsqlCommand`s for Large Object I/O, unchanged from #235) / xUnit + Testcontainers.PostgreSql / Blazor Server (`IBrowserFile.OpenReadStream()`).

## Global Constraints

- `IDocumentStorage.LoadAsync`/`DeleteAsync` do not change — only `SaveAsync`.
- `UploadZipAsync`'s internal zip-entry handling does not change — `ZipArchiveEntry` streams aren't re-openable from scratch, so entries stay `byte[]`-buffered via the existing `ReadBoundedAsync` exactly as today. Only its call into `UploadAsync` adapts, wrapping the already-buffered `byte[]` in `() => new MemoryStream(data)`.
- The exact existing Swedish error messages must be preserved: `"Filen är för stor (max 10 MB)."` for an oversized single file, `"Lagring misslyckades: {ex.Message}"` for any other storage failure.
- `DocumentTooLargeException` must not be retried by `CreateExecutionStrategy()` — it is a plain `Exception` subtype, not a type Npgsql's retrying strategy recognizes as transient, so it always propagates on the first attempt.
- This branch (`issue-236-stream-upload`) is already rebased onto current `origin/main` (commit `2236c38`), which includes #235's Large Object storage and #208's background-extraction decoupling. No further rebasing is needed before starting.
- Because `KoalaBooks.Tests` → `KoalaBooks.Web` → `KoalaBooks.Components` is a project-reference chain, `IDocumentStorage`'s signature change will not compile cleanly until every layer (`Domain`, `Infrastructure`, `Application`, and all four `Components` Razor call sites) is updated — there is no way to land this as independently-buildable increments without a throwaway compatibility shim, which is not worth the complexity for a change this contained. Treat Task 1 as one atomic unit; only the final step needs a green build.

---

### Task 1: Stream uploads through to storage via a factory

**Files:**
- Modify: `src/KoalaBooks.Domain/Interfaces/IDocumentStorage.cs`
- Modify: `src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs`
- Modify: `src/KoalaBooks.Application/Services/DocumentService.cs`
- Modify: `src/KoalaBooks.Components/Pages/Inbox.razor:277-278`
- Modify: `src/KoalaBooks.Components/Pages/CustomerInvoices.razor:699-702`
- Modify: `src/KoalaBooks.Components/Pages/SupplierInvoices.razor:669-672`
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor:593-596`
- Test: `tests/KoalaBooks.Tests/DbDocumentStorageTests.cs`
- Test: `tests/KoalaBooks.Tests/DbDocumentStorageRetryStrategyTests.cs`
- Test: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Produces: `IDocumentStorage.SaveAsync(int documentId, string contentType, Func<Stream> openData)` → `Task<(string StorageKey, long FileSize)>`. This is the only task in the plan; nothing downstream depends on it beyond what's listed here.
- Consumes: nothing — first and only implementation task.

- [ ] **Step 1: Update the `IDocumentStorage` interface**

Replace the full contents of `src/KoalaBooks.Domain/Interfaces/IDocumentStorage.cs`:

```csharp
namespace KoalaBooks.Domain.Interfaces;

public interface IDocumentStorage
{
    Task<(string StorageKey, long FileSize)> SaveAsync(int documentId, string contentType, Func<Stream> openData);
    Task<byte[]> LoadAsync(string storageKey);
    Task DeleteAsync(string storageKey);
}
```

- [ ] **Step 2: Write the new failing `DbDocumentStorageTests`**

Replace the full contents of `tests/KoalaBooks.Tests/DbDocumentStorageTests.cs`:

```csharp
using System.Data;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Services;
using Npgsql;
using NpgsqlTypes;

namespace KoalaBooks.Tests;

public class DbDocumentStorageTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task SaveAsync_AcceptsStreamFactoryAndRoundTripsThroughLoadAsync()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var doc = new Document
        {
            OrganisationId = _fx.OrganisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 3,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _fx.Db.Documents.Add(doc);
        await _fx.Db.SaveChangesAsync();

        var bytes = new byte[] { 1, 2, 3 };
        var (key, fileSize) = await storage.SaveAsync(doc.Id, "application/pdf", () => new MemoryStream(bytes));

        Assert.Equal(3, fileSize);
        var loaded = await storage.LoadAsync(key);
        Assert.Equal(bytes, loaded);
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingDataOnReupload()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var doc = new Document
        {
            OrganisationId = _fx.OrganisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 1,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _fx.Db.Documents.Add(doc);
        await _fx.Db.SaveChangesAsync();

        var (key, _) = await storage.SaveAsync(doc.Id, "application/pdf", () => new MemoryStream([1]));
        await storage.SaveAsync(doc.Id, "application/pdf", () => new MemoryStream([9, 9]));

        var loaded = await storage.LoadAsync(key);
        Assert.Equal(new byte[] { 9, 9 }, loaded);
    }

    [Fact]
    public async Task SaveAsync_WorksWithForwardOnlyNonSeekableStream()
    {
        // Guards against reintroducing type-special-casing that assumes a
        // concrete, seekable stream type instead of reading generically.
        var storage = new DbDocumentStorage(_fx.Db);
        var doc = new Document
        {
            OrganisationId = _fx.OrganisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 5,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _fx.Db.Documents.Add(doc);
        await _fx.Db.SaveChangesAsync();

        var bytes = new byte[] { 5, 4, 3, 2, 1 };
        var (key, fileSize) = await storage.SaveAsync(doc.Id, "application/pdf", () => new ForwardOnlyStream(new MemoryStream(bytes)));

        Assert.Equal(5, fileSize);
        var loaded = await storage.LoadAsync(key);
        Assert.Equal(bytes, loaded);
    }

    [Fact]
    public async Task DeleteAsync_UnlinksTheUnderlyingLargeObject()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var doc = new Document
        {
            OrganisationId = _fx.OrganisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 2,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _fx.Db.Documents.Add(doc);
        await _fx.Db.SaveChangesAsync();

        var (key, _) = await storage.SaveAsync(doc.Id, "application/pdf", () => new MemoryStream([7, 8]));
        var row = await _fx.Db.DocumentData.FindAsync(doc.Id);
        var oid = row!.Oid;

        await storage.DeleteAsync(key);

        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT lo_get(@oid)", conn);
        cmd.Parameters.Add(new NpgsqlParameter { ParameterName = "oid", NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 3: Write the new failing `DbDocumentStorageRetryStrategyTests`**

Replace the full contents of `tests/KoalaBooks.Tests/DbDocumentStorageRetryStrategyTests.cs`. The three tests in `DbDocumentStorageRetryStrategyTests` are mechanically updated (factory + tuple). `DbDocumentStorageForcedRetryTests` changes more substantially: `SaveAsync_ThrowsClearly_WhenRetriedWithANonSeekableSourceStream` is **deleted** — it asserted behavior (the `CanSeek` retry guard) that this task deletes outright, since a retry now re-invokes the factory instead of rewinding. `SaveAsync_RecoversFromAGenuineRetry_WhenSourceStreamIsSeekable` is replaced with `SaveAsync_RecoversFromAGenuineRetry_ByReinvokingTheFactory`, which proves the real mechanism: attempt 1's factory invocation returns a stream that always faults, attempt 2's invocation returns a fresh working stream, and the save still succeeds — no seekability involved at all:

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace KoalaBooks.Tests;

public class DbDocumentStorageRetryStrategyTests : IDisposable
{
    private readonly string _dbName;
    private readonly AppDbContext _db;
    private readonly int _organisationId;

    public DbDocumentStorageRetryStrategyTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        // Mirrors Program.cs's EnrichNpgsqlDbContext, which enables a
        // retrying execution strategy in the real app — this is what
        // DbDocumentStorage's manual transactions must be compatible with.
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
    public async Task SaveAsync_LoadAsync_DeleteAsync_WorkUnderRetryingExecutionStrategy()
    {
        var storage = new DbDocumentStorage(_db);
        var doc = new Document
        {
            OrganisationId = _organisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 3,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();

        var bytes = new byte[] { 1, 2, 3 };
        var (key, _) = await storage.SaveAsync(doc.Id, "application/pdf", () => new MemoryStream(bytes));

        var loaded = await storage.LoadAsync(key);
        Assert.Equal(bytes, loaded);

        await storage.DeleteAsync(key);
        var afterDelete = await storage.LoadAsync(key);
        Assert.Empty(afterDelete);
    }

    [Fact]
    public async Task SaveAsync_RecoversWhenAPriorFailedAttemptLeftDocumentDataTracked()
    {
        var storage = new DbDocumentStorage(_db);
        var doc = new Document
        {
            OrganisationId = _organisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 3,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();

        // Simulate what a retried attempt actually finds: a prior failed
        // SaveAsync got as far as tracking a DocumentData row (Added) with a
        // large object it created, but the transaction never committed. On
        // retry, EF's FindAsync would find this stale tracked entity locally
        // (before ever querying the DB) unless DetachTrackedDocumentData
        // clears it first.
        _db.DocumentData.Add(new DocumentData { DocumentId = doc.Id, Oid = 999999 });

        var bytes = new byte[] { 9, 9, 9 };
        var (key, _) = await storage.SaveAsync(doc.Id, "application/pdf", () => new MemoryStream(bytes));

        var loaded = await storage.LoadAsync(key);
        Assert.Equal(bytes, loaded);

        var count = await _db.DocumentData.CountAsync(d => d.DocumentId == doc.Id);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SaveAsync_DetachesTrackedDocumentDataWhenSaveChangesFailsAfterAdd()
    {
        var storage = new DbDocumentStorage(_db);

        // No Document row exists for this id. The Large Object writes
        // (lo_create/lowrite/lo_close) don't touch the Documents table, so they
        // succeed, and db.DocumentData.Add(...) tracks a new DocumentData as
        // Added — but SaveChangesAsync then fails with a foreign-key violation
        // (DocumentData.DocumentId has no matching Document) before the
        // transaction commits. This is exactly the scenario Item 1 fixes: an
        // exception thrown after the entity is Added/tracked but before commit.
        // The FK violation is also non-transient, so NpgsqlRetryingExecutionStrategy
        // won't retry it — it propagates straight out of SaveAsync, which is the
        // "all retries exhausted / non-transient failure" case the fix targets.
        var missingDocumentId = 999_999;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            storage.SaveAsync(missingDocumentId, "application/pdf", () => new MemoryStream([1, 2, 3])));

        Assert.DoesNotContain(_db.ChangeTracker.Entries<DocumentData>(),
            e => e.Entity.DocumentId == missingDocumentId);
    }
}

/// <summary>
/// Drives a genuine retry of the whole SaveAsync delegate (not just a
/// pre-seeded stale-tracked-entity scenario) by installing an execution
/// strategy that unconditionally retries once, and a factory whose first
/// invocation returns a stream that always faults. This exercises the real
/// DetachTrackedDocumentData + factory-reinvocation recovery path under an
/// actual second invocation of the ExecuteAsync delegate — not a simulation
/// of its aftermath.
/// </summary>
public class DbDocumentStorageForcedRetryTests : IDisposable
{
    private readonly string _dbName;
    private readonly AppDbContext _db;
    private readonly int _organisationId;

    public DbDocumentStorageForcedRetryTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr, o => o.ExecutionStrategy(deps => new AlwaysRetryOnceExecutionStrategy(deps)))
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
    public async Task SaveAsync_RecoversFromAGenuineRetry_ByReinvokingTheFactory()
    {
        var storage = new DbDocumentStorage(_db);
        var doc = new Document
        {
            OrganisationId = _organisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 4,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();

        var bytes = new byte[] { 4, 3, 2, 1 };
        var invocations = 0;
        Stream OpenData()
        {
            invocations++;
            return invocations == 1 ? new FaultingStream() : new MemoryStream(bytes);
        }

        var (key, fileSize) = await storage.SaveAsync(doc.Id, "application/pdf", OpenData);

        // Proves a genuine second attempt actually re-invoked the factory,
        // rather than the save succeeding without ever retrying.
        Assert.True(invocations > 1, "expected the factory to be re-invoked after the simulated fault");
        Assert.Equal(4, fileSize);
        var loaded = await storage.LoadAsync(key);
        Assert.Equal(bytes, loaded);
    }

    /// <summary>A stream that throws on every read, simulating a mid-write transient failure.</summary>
    private sealed class FaultingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("Simulated transient failure mid-write.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("Simulated transient failure mid-write.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Retries exactly once on the simulated transient IOException from
    /// FaultingStream, mirroring how NpgsqlRetryingExecutionStrategy retries only
    /// exceptions it classifies as transient. This deterministically drives a second
    /// invocation of SaveAsync's delegate without depending on provoking a genuine
    /// Postgres-classified transient failure.</summary>
    private sealed class AlwaysRetryOnceExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is IOException;
    }
}
```

- [ ] **Step 4: Run the build to verify it fails**

Run: `dotnet build tests/KoalaBooks.Tests`
Expected: BUILD FAILS. `DbDocumentStorage.cs` still declares `SaveAsync(int documentId, string contentType, Stream data)` returning `Task<string>`, which no longer matches `IDocumentStorage` or the test calls above.

- [ ] **Step 5: Implement `DbDocumentStorage.SaveAsync` using the factory**

In `src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs`, replace the `SaveAsync` method (the guard block that checks `data.CanSeek`/throws for non-seekable streams is deleted entirely — a retry now just calls `openData()` again):

```csharp
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
                    await ExecuteScalarAsync<int>(conn, "SELECT lo_unlink(@oid)", ("oid", NpgsqlDbType.Oid, existing.Oid));

                var oid = await ExecuteScalarAsync<uint>(conn, "SELECT lo_create(0)");
                var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
                    ("oid", NpgsqlDbType.Oid, oid), ("mode", NpgsqlDbType.Integer, InvWrite));

                var buffer = new byte[ChunkSize];
                long fileSize = 0;
                int read;
                while ((read = await data.ReadAsync(buffer)) > 0)
                {
                    fileSize += read;
                    var chunk = buffer[..read];
                    await ExecuteScalarAsync<int>(conn, "SELECT lowrite(@fd, @chunk)",
                        ("fd", NpgsqlDbType.Integer, fd), ("chunk", NpgsqlDbType.Bytea, chunk));
                }
                await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd));

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
```

`LoadAsync`, `DeleteAsync`, `DetachTrackedDocumentData`, and `ExecuteScalarAsync` are unchanged — leave them exactly as they are in the file.

- [ ] **Step 6: Write the new failing `DocumentServiceTests`**

Replace the full contents of `tests/KoalaBooks.Tests/DocumentServiceTests.cs`. Every `new MemoryStream(...)` argument to `UploadAsync`/`UploadAndLinkAsync` is wrapped in a factory lambda (`() => new MemoryStream(...)`); `UploadZipAsync` calls are untouched since its `byte[]` signature doesn't change. `FailingStorage.SaveAsync` is updated to the new signature and return type. `UploadAsync_RejectsOversizedFile` gains assertions for the exact error message and that no `Document` row survives — this is genuinely new behavior worth locking down: previously the size check happened *before* any `Document` row was created (in `ReadBoundedAsync`, ahead of `db.Documents.Add(doc)`); now it happens mid-`SaveAsync`, after the row already exists, so it needs the same rollback `UploadAsync_RollsBackDocumentRowWhenStorageFails` already covers for other storage failures:

```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Tests;

public class DocumentServiceTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task UploadAsync_StoresDocumentAndReturnsIt()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, err) = await svc.UploadAsync("faktura.pdf", "application/pdf", () => new MemoryStream(new byte[] { 1, 2, 3 }));

        Assert.Null(err);
        Assert.NotNull(doc);
        Assert.Equal("faktura.pdf", doc.FileName);
        Assert.Equal(3, doc.FileSize);
        Assert.NotEmpty(doc.StorageKey);
    }

    [Fact]
    public async Task UploadAsync_SetsExtractionStatusPending_NoSuggestionYet()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("leverantörsfaktura.pdf", "application/pdf", () => new MemoryStream());

        Assert.Equal(ExtractionStatus.Pending, doc!.ExtractionStatus);
        Assert.Null(doc.SuggestedType);
        Assert.Null(doc.ClassifiedType);
    }

    [Fact]
    public async Task UploadAsync_RejectsDisallowedContentType()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, err) = await svc.UploadAsync("bad.html", "text/html", () => new MemoryStream([1, 2, 3]));

        Assert.Null(doc);
        Assert.NotNull(err);
    }

    [Fact]
    public async Task UploadAsync_RejectsOversizedFile()
    {
        var svc = _fx.MakeDocumentService();
        var bigData = new byte[11 * 1024 * 1024];
        var (doc, err) = await svc.UploadAsync("big.pdf", "application/pdf", () => new MemoryStream(bigData));

        Assert.Null(doc);
        Assert.Equal("Filen är för stor (max 10 MB).", err);

        // The size cap is now enforced mid-SaveAsync (after the Document row
        // already exists), not upfront — must roll back the same as any other
        // storage failure, not leave an orphaned row behind.
        var pending = await _fx.MakeDocumentService().GetPendingAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task GetPendingAsync_ReturnsOnlyUnlinkedDocuments()
    {
        var svc = _fx.MakeDocumentService();
        var fy = _fx.CreateFiscalYear();
        var (debit, credit, _, _, _) = _fx.CreateStandardAccounts(fy.Id);
        var entry = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 100m);

        await svc.UploadAsync("unlinked.pdf", "application/pdf", () => new MemoryStream([1]));
        var (linked, _) = await svc.UploadAsync("linked.pdf", "application/pdf", () => new MemoryStream([2]));
        await svc.LinkAsync(linked!.Id, DocumentEntityType.JournalEntry, entry.Id);

        var pending = await svc.GetPendingAsync();

        Assert.Single(pending);
        Assert.Equal("unlinked.pdf", pending[0].FileName);
    }

    [Fact]
    public async Task UpdateMetadataAsync_SetsTypeAndDate()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("unknown.pdf", "application/pdf", () => new MemoryStream());
        var date = new DateOnly(2026, 3, 15);

        var err = await svc.UpdateMetadataAsync(doc!.Id, "CustomerInvoice", date);

        Assert.Null(err);
        var pending = await svc.GetPendingAsync();
        var updated = pending.First(d => d.Id == doc.Id);
        Assert.Equal("CustomerInvoice", updated.ClassifiedType);
        Assert.Equal(date, updated.DocumentDate);
    }

    [Fact]
    public async Task UploadAsync_EnqueuesExtractionJob()
    {
        var queue = new RecordingExtractionQueue();
        var svc = _fx.MakeDocumentService(queue);

        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", () => new MemoryStream([1, 2, 3]));

        Assert.Equal(doc!.Id, Assert.Single(queue.EnqueuedDocumentIds));
    }

    [Fact]
    public async Task GetPendingAsync_SortsByDocumentDate()
    {
        var svc = _fx.MakeDocumentService();
        var (d1, _) = await svc.UploadAsync("a.pdf", "application/pdf", () => new MemoryStream([1]));
        var (d2, _) = await svc.UploadAsync("b.pdf", "application/pdf", () => new MemoryStream([2]));

        await svc.UpdateMetadataAsync(d1!.Id, null, new DateOnly(2026, 1, 1));
        await svc.UpdateMetadataAsync(d2!.Id, null, new DateOnly(2026, 6, 1));

        var ascResult = await svc.GetPendingAsync(sortBy: "documentDate", sortAsc: true);
        Assert.Equal(d1.Id, ascResult[0].Id);
        Assert.Equal(d2.Id, ascResult[1].Id);

        var descResult = await svc.GetPendingAsync(sortBy: "documentDate", sortAsc: false);
        Assert.Equal(d2.Id, descResult[0].Id);
        Assert.Equal(d1.Id, descResult[1].Id);
    }

    [Fact]
    public async Task GetLinkedAsync_ReturnsDocumentsForJournalEntry()
    {
        var svc = _fx.MakeDocumentService();
        var fy = _fx.CreateFiscalYear();
        var (debit, credit, _, _, _) = _fx.CreateStandardAccounts(fy.Id);
        var entry = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 100m);

        var (doc, _) = await svc.UploadAsync("receipt.pdf", "application/pdf", () => new MemoryStream([5]));
        await svc.LinkAsync(doc!.Id, DocumentEntityType.JournalEntry, entry.Id);

        var linked = await svc.GetLinkedAsync(DocumentEntityType.JournalEntry, entry.Id);

        Assert.Single(linked);
        Assert.Equal("receipt.pdf", linked[0].FileName);
    }

    [Fact]
    public async Task DeleteAsync_RemovesDocumentAndData()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("todelete.pdf", "application/pdf", () => new MemoryStream([9, 8, 7]));

        var deleted = await svc.DeleteAsync(doc!.Id);
        Assert.True(deleted);

        var pending = await svc.GetPendingAsync();
        Assert.Empty(pending);

        var download = await svc.GetDownloadAsync(doc.Id);
        Assert.Null(download);
    }

    [Fact]
    public async Task GetDownloadAsync_ReturnsBytesForUploadedDocument()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("file.pdf", "application/pdf", () => new MemoryStream([10, 20, 30]));

        var result = await svc.GetDownloadAsync(doc!.Id);

        Assert.NotNull(result);
        Assert.Equal("application/pdf", result.Value.ContentType);
        Assert.Equal(new byte[] { 10, 20, 30 }, result.Value.Data);
    }

    [Fact]
    public async Task UploadAsync_RollsBackDocumentRowWhenStorageFails()
    {
        var svc = _fx.MakeDocumentService(new FailingStorage());
        var (doc, err) = await svc.UploadAsync("faktura.pdf", "application/pdf", () => new MemoryStream([1, 2, 3]));

        Assert.Null(doc);
        Assert.NotNull(err);

        // The metadata row must not survive the storage failure
        var pending = await _fx.MakeDocumentService().GetPendingAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task UploadAsync_AcceptsImageJpgMimeType()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, err) = await svc.UploadAsync("photo.jpg", "image/jpg", () => new MemoryStream([1, 2, 3]));

        Assert.Null(err);
        Assert.NotNull(doc);
    }

    [Fact]
    public async Task PostSupplierInvoice_AutoLinksDocumentToJournalEntry()
    {
        var docSvc = _fx.MakeDocumentService();
        var supplierSvc = new SupplierInvoiceService(_fx.Db);
        var fy = _fx.CreateFiscalYear();
        var (expense, payable, _, _, _) = _fx.CreateStandardAccounts(fy.Id);

        var invoice = new SupplierInvoice
        {
            FiscalYearId = fy.Id,
            SupplierName = "ACME AB",
            InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
            DueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(30)),
            AmountExclVat = 800m,
            VatAmount = 200m,
            TotalAmount = 1000m
        };
        var (created, _) = await supplierSvc.CreateAsync(invoice);

        var (doc, _) = await docSvc.UploadAsync("faktura.pdf", "application/pdf", () => new MemoryStream([1]));
        await docSvc.LinkAsync(doc!.Id, DocumentEntityType.SupplierInvoice, created!.Id);

        var (posted, err) = await supplierSvc.PostAsync(created.Id, expense.Id, payable.Id, null);
        Assert.Null(err);

        var linked = await docSvc.GetLinkedAsync(DocumentEntityType.JournalEntry, posted!.JournalEntryId!.Value);
        Assert.Single(linked);
        Assert.Equal("faktura.pdf", linked[0].FileName);
    }

    [Fact]
    public async Task GetCountsForJournalEntriesAsync_CountsCorrectly()
    {
        var svc = _fx.MakeDocumentService();
        var fy = _fx.CreateFiscalYear();
        var (debit, credit, _, _, _) = _fx.CreateStandardAccounts(fy.Id);
        var e1 = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 100m);
        var e2 = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 200m);

        var (doc1, _) = await svc.UploadAsync("a.pdf", "application/pdf", () => new MemoryStream([1]));
        var (doc2, _) = await svc.UploadAsync("b.pdf", "application/pdf", () => new MemoryStream([2]));
        var (doc3, _) = await svc.UploadAsync("c.pdf", "application/pdf", () => new MemoryStream([3]));

        await svc.LinkAsync(doc1!.Id, DocumentEntityType.JournalEntry, e1.Id);
        await svc.LinkAsync(doc2!.Id, DocumentEntityType.JournalEntry, e1.Id);
        await svc.LinkAsync(doc3!.Id, DocumentEntityType.JournalEntry, e2.Id);

        var counts = await svc.GetCountsForJournalEntriesAsync([e1.Id, e2.Id]);

        Assert.Equal(2, counts[e1.Id]);
        Assert.Equal(1, counts[e2.Id]);
    }

    [Fact]
    public async Task UploadZipAsync_ImportsAllValidEntries()
    {
        var svc = _fx.MakeDocumentService();
        var zip = BuildZip(("a.pdf", new byte[] { 1, 2, 3 }), ("b.png", new byte[] { 4, 5 }));

        var (result, err) = await svc.UploadZipAsync(zip);

        Assert.Null(err);
        Assert.NotNull(result);
        Assert.Equal(2, result.Imported.Count);
        Assert.Contains(result.Imported, d => d.FileName == "a.pdf");
        Assert.Contains(result.Imported, d => d.FileName == "b.png");
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task UploadZipAsync_FlattensNestedFolderPaths()
    {
        var svc = _fx.MakeDocumentService();
        var zip = BuildZip(("invoices/2026/faktura.pdf", new byte[] { 1, 2, 3 }));

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Single(result!.Imported);
        Assert.Equal("faktura.pdf", result.Imported[0].FileName);
    }

    [Fact]
    public async Task UploadZipAsync_SkipsDirectoryEntries()
    {
        var svc = _fx.MakeDocumentService();
        var zip = BuildZipWithDirectoryEntry();

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Single(result!.Imported);
        Assert.Equal("faktura.pdf", result.Imported[0].FileName);
    }

    [Fact]
    public async Task UploadZipAsync_SkipsInvalidEntriesAndReportsReasons()
    {
        var svc = _fx.MakeDocumentService();
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.exe", new byte[] { 1, 2, 3 }));

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Single(result!.Imported);
        Assert.Equal("good.pdf", result.Imported[0].FileName);
        Assert.Single(result.Skipped);
        Assert.Equal("bad.exe", result.Skipped[0].FileName);
    }

    [Fact]
    public async Task UploadZipAsync_SkipsOversizedEntry()
    {
        var svc = _fx.MakeDocumentService();
        var bigData = new byte[11 * 1024 * 1024];
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("big.pdf", bigData));

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Single(result!.Imported);
        Assert.Equal("good.pdf", result.Imported[0].FileName);
        Assert.Single(result.Skipped);
        Assert.Equal("big.pdf", result.Skipped[0].FileName);
    }

    [Fact]
    public async Task UploadZipAsync_SkipsEntryWhenStorageFails_RestOfBatchStillImports()
    {
        var svc = _fx.MakeDocumentService(new FailingStorage());
        var zip = BuildZip(("faktura.pdf", new byte[] { 1, 2, 3 }));

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Empty(result!.Imported);
        Assert.Single(result.Skipped);
        Assert.Equal("faktura.pdf", result.Skipped[0].FileName);
    }

    [Fact]
    public async Task UploadZipAsync_RejectsOversizedZipContainer()
    {
        var svc = _fx.MakeDocumentService();
        var bigZip = new byte[51 * 1024 * 1024];

        var (result, err) = await svc.UploadZipAsync(bigZip);

        Assert.Null(result);
        Assert.NotNull(err);
    }

    [Fact]
    public async Task UploadZipAsync_RejectsZipWithTooManyEntries()
    {
        var svc = _fx.MakeDocumentService();
        var entries = Enumerable.Range(1, 51)
            .Select(i => ($"file{i}.pdf", new byte[] { 1 }))
            .ToArray();
        var zip = BuildZip(entries);

        var (result, err) = await svc.UploadZipAsync(zip);

        Assert.Null(result);
        Assert.NotNull(err);
    }

    [Fact]
    public async Task UploadZipAsync_RejectsCorruptZipFile()
    {
        var svc = _fx.MakeDocumentService();
        var corruptBytes = new byte[] { 1, 2, 3, 4, 5 };

        var (result, err) = await svc.UploadZipAsync(corruptBytes);

        Assert.Null(result);
        Assert.NotNull(err);
    }

    [Fact]
    public async Task UploadZipAsync_SkipsCorruptEntry_RestOfBatchStillImports()
    {
        var svc = _fx.MakeDocumentService();
        var zip = CorruptEntryData(BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.pdf", new byte[500])), "bad.pdf");

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Single(result!.Imported);
        Assert.Equal("good.pdf", result.Imported[0].FileName);
        Assert.Single(result.Skipped);
        Assert.Equal("bad.pdf", result.Skipped[0].FileName);
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

    private static byte[] BuildZipWithDirectoryEntry()
    {
        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("empty_folder/");
            var entry = archive.CreateEntry("faktura.pdf");
            using var entryStream = entry.Open();
            var data = new byte[] { 1, 2, 3 };
            entryStream.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }
}

file class FailingStorage : IDocumentStorage
{
    public Task<(string StorageKey, long FileSize)> SaveAsync(int documentId, string contentType, Func<Stream> openData) =>
        throw new InvalidOperationException("simulated storage failure");

    public Task<byte[]> LoadAsync(string storageKey) => Task.FromResult(Array.Empty<byte>());
    public Task DeleteAsync(string storageKey) => Task.CompletedTask;
}

file class RecordingExtractionQueue : IDocumentExtractionQueue
{
    public List<int> EnqueuedDocumentIds { get; } = [];
    public void Enqueue(int documentId) => EnqueuedDocumentIds.Add(documentId);
}
```

- [ ] **Step 7: Run the build to verify it still fails**

Run: `dotnet build tests/KoalaBooks.Tests`
Expected: BUILD FAILS. `DocumentService.cs` still declares `UploadAsync(string fileName, string contentType, Stream data)` and calls `storage.SaveAsync(doc.Id, contentType, new MemoryStream(bytes))` with the old signature.

- [ ] **Step 8: Implement the `DocumentService` changes**

In `src/KoalaBooks.Application/Services/DocumentService.cs`, insert two private nested classes immediately after the `ZipEntryContentTypes` dictionary (after line 34, before `UploadAsync`):

```csharp
    private sealed class DocumentTooLargeException : Exception;

    private sealed class MaxBytesEnforcingStream(Stream inner, long maxBytes) : Stream
    {
        private long _totalRead;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _totalRead += read;
            if (_totalRead > maxBytes) throw new DocumentTooLargeException();
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
```

Then replace the `UploadAsync` method:

```csharp
    public async Task<(Document? Doc, string? Error)> UploadAsync(string fileName, string contentType, Func<Stream> openData)
    {
        if (currentUser.OrganisationId is null)
            return (null, "Ingen aktiv organisation.");
        if (!AllowedContentTypes.Contains(contentType))
            return (null, "Otillåten filtyp. Tillåtna typer: PDF, PNG, JPEG.");

        var doc = new Document
        {
            OrganisationId = currentUser.OrganisationId.Value,
            FileName = fileName,
            ContentType = contentType,
            FileSize = 0,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync(); // gets doc.Id

        try
        {
            (doc.StorageKey, doc.FileSize) = await storage.SaveAsync(
                doc.Id, contentType, () => new MaxBytesEnforcingStream(openData(), MaxBytes));
        }
        catch (DocumentTooLargeException)
        {
            db.Documents.Remove(doc);
            await db.SaveChangesAsync();
            return (null, "Filen är för stor (max 10 MB).");
        }
        catch (Exception ex)
        {
            // Storage failed — roll back the DB row to avoid orphaned metadata
            db.Documents.Remove(doc);
            await db.SaveChangesAsync();
            return (null, $"Lagring misslyckades: {ex.Message}");
        }

        doc.ExtractionStatus = ExtractionStatus.Pending;
        await db.SaveChangesAsync();
        extractionQueue.Enqueue(doc.Id);

        return (doc, null);
    }
```

Then change the `UploadAndLinkAsync` signature line (only the parameter type changes, the body is untouched):

```csharp
    public async Task<(Document? Doc, string? Error)> UploadAndLinkAsync(
        string fileName, string contentType, Func<Stream> openData, DocumentEntityType entityType, int entityId)
    {
        var (doc, err) = await UploadAsync(fileName, contentType, openData);
```

Then change the `UploadAsync` call inside `UploadZipAsync`'s entry loop:

```csharp
                var (doc, err) = await UploadAsync(entry.Name, contentType, () => new MemoryStream(data));
```

`ReadBoundedAsync` is unchanged — it's still used by `UploadZipAsync` for reading each zip entry.

- [ ] **Step 9: Run the build to verify it still fails**

Run: `dotnet build tests/KoalaBooks.Tests`
Expected: BUILD FAILS. `Inbox.razor`, `CustomerInvoices.razor`, `SupplierInvoices.razor`, and `Journal.razor` still call `UploadAsync`/`UploadAndLinkAsync` with an already-opened `Stream` instead of a `Func<Stream>` — `KoalaBooks.Tests` transitively references `KoalaBooks.Web` → `KoalaBooks.Components`, so these must compile too.

- [ ] **Step 10: Update the four Razor call sites**

In `src/KoalaBooks.Components/Pages/Inbox.razor`, replace lines 277-278:

```csharp
                var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
                var (_, err) = await DocumentService.UploadAsync(file.Name, contentType, () => file.OpenReadStream(fileMaxBytes));
```

(This removes the `await using var stream = file.OpenReadStream(fileMaxBytes);` local — `DbDocumentStorage.SaveAsync` now owns opening and disposing the stream per attempt.)

In `src/KoalaBooks.Components/Pages/CustomerInvoices.razor`, replace lines 699-702:

```csharp
            var (doc, err) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, () => e.File.OpenReadStream(maxBytes),
                DocumentEntityType.CustomerInvoice, _docPanelInvoiceId!.Value);
```

In `src/KoalaBooks.Components/Pages/SupplierInvoices.razor`, replace lines 669-672:

```csharp
            var (doc, err) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, () => e.File.OpenReadStream(maxBytes),
                DocumentEntityType.SupplierInvoice, _docPanelInvoiceId!.Value);
```

In `src/KoalaBooks.Components/Pages/Journal.razor`, replace lines 593-596:

```csharp
            var (added, uploadErr) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, () => e.File.OpenReadStream(maxBytes),
                DocumentEntityType.JournalEntry, _attachmentEntryId!.Value);
```

- [ ] **Step 11: Run the full build to verify it succeeds**

Run: `dotnet build`
Expected: BUILD SUCCEEDS with no errors.

- [ ] **Step 12: Run the targeted tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~DocumentServiceTests|FullyQualifiedName~DbDocumentStorage"`
Expected: PASS, all tests in `DocumentServiceTests`, `DbDocumentStorageTests`, `DbDocumentStorageRetryStrategyTests`, and `DbDocumentStorageForcedRetryTests` green.

- [ ] **Step 13: Run the full test suite to verify nothing else broke**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS, full suite green (this includes `DocumentExtractionJobTests`, which only exercises `storage.LoadAsync` and is unaffected by this change, but must still compile against the new `IDocumentStorage`).

Run: `dotnet test tests/KoalaBooks.ComponentTests`
Expected: PASS. `PreviewDocumentDialogTests.cs` constructs `Substitute.For<IDocumentStorage>()` with no `SaveAsync` stubbing, so it adapts to the new interface shape automatically.

- [ ] **Step 14: Commit**

```bash
git add src/KoalaBooks.Domain/Interfaces/IDocumentStorage.cs \
        src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs \
        src/KoalaBooks.Application/Services/DocumentService.cs \
        src/KoalaBooks.Components/Pages/Inbox.razor \
        src/KoalaBooks.Components/Pages/CustomerInvoices.razor \
        src/KoalaBooks.Components/Pages/SupplierInvoices.razor \
        src/KoalaBooks.Components/Pages/Journal.razor \
        tests/KoalaBooks.Tests/DbDocumentStorageTests.cs \
        tests/KoalaBooks.Tests/DbDocumentStorageRetryStrategyTests.cs \
        tests/KoalaBooks.Tests/DocumentServiceTests.cs
git commit -m "feat: stream uploads straight through to storage via a Func<Stream> factory

Removes DocumentService.UploadAsync's last full-file byte[] buffer.
DbDocumentStorage.SaveAsync now calls the factory fresh on every
transient-failure retry instead of rewinding a seekable stream, so
the old CanSeek/non-seekable-throw guard is gone. MaxBytesEnforcingStream
enforces the 10 MB cap while streaming instead of upfront."
```

---

### Task 2: Manual verification against the running app

**Files:** none (verification only).

**Interfaces:** none — this task only exercises the app end-to-end.

- [ ] **Step 1: Start the app**

Use the project's `run` skill (or `aspire start` per the `aspire` skill) to launch KoalaBooks locally against a real Postgres.

- [ ] **Step 2: Upload a normal-sized document at each of the four call sites**

In the browser: upload a PDF or image via Inbox, then via a document-attachment panel on CustomerInvoices, SupplierInvoices, and Journal.

Expected: each upload succeeds with no error, the document appears in the relevant list, and downloading/previewing it afterward shows byte-identical, correctly-rendering content.

- [ ] **Step 3: Upload an oversized file via Inbox**

Select a file larger than 10 MB for a single (non-zip) upload in Inbox.

Expected: upload fails with "Filen är för stor (max 10 MB)." and the file does **not** appear in the pending list afterward (confirms the mid-stream rollback works against the real app, not just the unit test).

- [ ] **Step 4: Re-run the zip import**

Upload a `.zip` file containing a mix of valid and invalid entries via Inbox's zip-import path.

Expected: behaves exactly as before this change — valid entries import, invalid/oversized entries are reported as skipped with reasons. This path is unchanged by this plan; the check confirms it.
