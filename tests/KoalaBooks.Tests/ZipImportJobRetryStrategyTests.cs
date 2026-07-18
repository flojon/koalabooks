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

        var runService = new BackgroundJobRunService(_db, _dbOptions, new LocalCurrentUser(_organisationId));
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
