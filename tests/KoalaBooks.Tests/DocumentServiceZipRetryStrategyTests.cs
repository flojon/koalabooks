using KoalaBooks.Application.Jobs;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
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
        var svc = new DocumentService(_db, new DbDocumentStorage(_db),
            new NoOpDocumentExtractionQueue(), new NoOpZipImportQueue(),
            new LocalCurrentUser(_organisationId));

        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("a.pdf");
            using var entryStream = entry.Open();
            entryStream.Write([1, 2, 3]);
        }
        var zipBytes = ms.ToArray();

        var (batchId, err) = await svc.UploadZipAsync(() => new MemoryStream(zipBytes));

        Assert.Null(err);
        Assert.NotNull(batchId);

        // _db's currentUser has no active org, so bypass the tenant query filter.
        var batch = await _db.ZipImportBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        Assert.NotNull(batch.StagingOid);
    }
}
