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
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly AppDbContext _db;
    private readonly int _organisationId;

    public DocumentServiceZipRetryStrategyTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        // Mirrors Program.cs's EnrichNpgsqlDbContext, which enables a
        // retrying execution strategy in the real app — this is what
        // UploadZipAsync's manual staging transaction must be compatible with.
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
    public async Task UploadZipAsync_StagesZip_UnderRetryingExecutionStrategy()
    {
        var currentUser = new LocalCurrentUser(_organisationId);
        var queue = new RecordingZipImportQueue();
        var svc = new DocumentService(_db, new DbDocumentStorage(_db),
            new NoOpDocumentExtractionQueue(), queue,
            new BackgroundJobRunService(_db, _dbOptions, currentUser), currentUser);

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
