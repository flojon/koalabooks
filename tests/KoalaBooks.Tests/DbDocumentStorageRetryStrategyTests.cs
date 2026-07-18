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
    private readonly LocalCurrentUser _currentUser;
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

        _currentUser = new LocalCurrentUser();
        _db = new AppDbContext(options, _currentUser);
        _db.Database.EnsureCreated();

        var org = new Organisation { Name = "Test Org", Slug = "test-org" };
        _db.Organisations.Add(org);
        _db.SaveChanges();
        _organisationId = org.Id;
        _currentUser.OrganisationId = _organisationId;
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
