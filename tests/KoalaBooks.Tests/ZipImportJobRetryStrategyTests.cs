using KoalaBooks.Application.Jobs;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
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
    // Only used to seed the org and stage the zip, and to re-read the batch afterwards —
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
    public async Task RunAsync_ProcessesStagedBatch_UnderRetryingExecutionStrategy()
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

        var batch = new ZipImportBatch { OrganisationId = _organisationId, StagingOid = stagingOid, TotalEntries = 1 };
        _db.ZipImportBatches.Add(batch);
        await _db.SaveChangesAsync();

        var job = new ZipImportJob(_dbOptions, new DbDocumentStorage(_db), new NoOpDocumentExtractionQueue(),
            new NoOpZipImportQueue(), NullLogger<ZipImportJob>.Instance);

        await job.RunAsync(batch.Id);

        // AsNoTracking: batch is already tracked from the setup above, so a tracking
        // query would return the identity-mapped in-memory instance rather than
        // re-reading the actual persisted row — masking a failed SaveChangesAsync
        // that never reached Postgres.
        var updated = await _db.ZipImportBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batch.Id);
        Assert.True(updated.Done);
        Assert.Equal(1, updated.ImportedCount);
        Assert.Null(updated.StagingOid);
    }
}
