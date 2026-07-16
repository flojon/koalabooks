using System.Data;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
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
