// tests/KoalaBooks.Tests/PostgresLargeObjectsTests.cs
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KoalaBooks.Tests;

public class PostgresLargeObjectsTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task CopyStreamIntoNewLargeObjectAsync_ThenCopyLargeObjectIntoStreamAsync_RoundTripsBytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 250, 251, 252 };
        await using var tx = await _fx.Db.Database.BeginTransactionAsync();
        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();

        var (oid, length) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, new MemoryStream(bytes));
        Assert.Equal(bytes.Length, length);

        using var readBack = new MemoryStream();
        await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, oid, readBack);
        Assert.Equal(bytes, readBack.ToArray());

        await PostgresLargeObjects.DeleteLargeObjectAsync(conn, oid);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task CopyStreamIntoNewLargeObjectAsync_HandlesEmptyStream()
    {
        await using var tx = await _fx.Db.Database.BeginTransactionAsync();
        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();

        var (oid, length) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, new MemoryStream());
        Assert.Equal(0, length);

        using var readBack = new MemoryStream();
        await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, oid, readBack);
        Assert.Empty(readBack.ToArray());

        await tx.CommitAsync();
    }

    [Fact]
    public async Task CopyStreamIntoNewLargeObjectAsync_HandlesDataLargerThanChunkSize()
    {
        var bytes = new byte[200_000]; // larger than the 80KB chunk size used internally
        new Random(42).NextBytes(bytes);
        await using var tx = await _fx.Db.Database.BeginTransactionAsync();
        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();

        var (oid, length) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, new MemoryStream(bytes));
        Assert.Equal(bytes.Length, length);

        using var readBack = new MemoryStream();
        await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, oid, readBack);
        Assert.Equal(bytes, readBack.ToArray());

        await tx.CommitAsync();
    }
}
