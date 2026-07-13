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
        var key = await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream(bytes));

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
        var key = await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream(bytes));

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
            storage.SaveAsync(missingDocumentId, "application/pdf", new MemoryStream([1, 2, 3])));

        Assert.DoesNotContain(_db.ChangeTracker.Entries<DocumentData>(),
            e => e.Entity.DocumentId == missingDocumentId);
    }
}

/// <summary>
/// Drives a genuine retry of the whole SaveAsync delegate (not just a
/// pre-seeded stale-tracked-entity scenario) by installing an execution
/// strategy that unconditionally retries once, and a source stream that
/// throws on its first read. This exercises the real
/// DetachTrackedDocumentData + stream-rewind recovery path, and the new
/// non-seekable-stream retry guard, under an actual second invocation of
/// the ExecuteAsync delegate — not a simulation of its aftermath.
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
    public async Task SaveAsync_RecoversFromAGenuineRetry_WhenSourceStreamIsSeekable()
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
        var faultOnceStream = new FaultOnceStream(new MemoryStream(bytes), seekable: true);

        var key = await storage.SaveAsync(doc.Id, "application/pdf", faultOnceStream);

        // Proves a genuine second attempt actually re-read the stream from the
        // start, rather than the save succeeding without ever retrying.
        Assert.True(faultOnceStream.ReadAttempts > 1,
            "expected the source stream to be read again after the simulated fault");
        var loaded = await storage.LoadAsync(key);
        Assert.Equal(bytes, loaded);
    }

    [Fact]
    public async Task SaveAsync_ThrowsClearly_WhenRetriedWithANonSeekableSourceStream()
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
        var faultOnceStream = new FaultOnceStream(new MemoryStream(bytes), seekable: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.SaveAsync(doc.Id, "application/pdf", faultOnceStream));
        Assert.Contains("not seekable", ex.Message);

        // No half-written DocumentData row should have been left behind or committed.
        var row = await _db.DocumentData.FindAsync(doc.Id);
        Assert.Null(row);
    }

    /// <summary>Wraps a stream, throwing once on its first read to simulate a mid-write transient failure.</summary>
    private sealed class FaultOnceStream(Stream inner, bool seekable) : Stream
    {
        private bool _hasFaulted;

        public int ReadAttempts { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => seekable ? inner.Position : throw new NotSupportedException();
            set
            {
                if (!seekable) throw new NotSupportedException();
                inner.Position = value;
            }
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadAttempts++;
            if (!_hasFaulted)
            {
                _hasFaulted = true;
                throw new IOException("Simulated transient failure mid-write.");
            }
            return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            seekable ? inner.Seek(offset, origin) : throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Retries exactly once on the simulated transient IOException from
    /// FaultOnceStream, mirroring how NpgsqlRetryingExecutionStrategy retries only
    /// exceptions it classifies as transient — deliberately does NOT retry the new
    /// InvalidOperationException guard, so that exception propagates directly instead
    /// of being wrapped in a RetryLimitExceededException once retries are exhausted.
    /// This deterministically drives a second invocation of SaveAsync's delegate
    /// without depending on provoking a genuine Postgres-classified transient failure.</summary>
    private sealed class AlwaysRetryOnceExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is IOException;
    }
}
