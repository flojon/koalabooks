using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

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
