using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Services;

namespace KoalaBooks.Tests;

public class DbDocumentStorageTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task SaveAsync_AcceptsStreamAndRoundTripsThroughLoadAsync()
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
        var key = await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream(bytes));

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

        var key = await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream([1]));
        await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream([9, 9]));

        var loaded = await storage.LoadAsync(key);
        Assert.Equal(new byte[] { 9, 9 }, loaded);
    }
}
