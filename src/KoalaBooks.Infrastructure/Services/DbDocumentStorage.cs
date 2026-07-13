// src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace KoalaBooks.Infrastructure.Services;

public class DbDocumentStorage(AppDbContext db) : IDocumentStorage
{
    // https://www.postgresql.org/docs/current/lo-interfaces.html#LO-INTERFACES-OPEN
    private const int InvWrite = 0x00020000;
    private const int InvRead = 0x00040000;
    private const int ChunkSize = 81920; // matches Stream.CopyToAsync's default buffer size

    public async Task<string> SaveAsync(int documentId, string contentType, Stream data)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        var existing = await db.DocumentData.FindAsync(documentId);
        if (existing is not null)
            await ExecuteScalarAsync<int>(conn, "SELECT lo_unlink(@oid)", ("oid", NpgsqlDbType.Oid, existing.Oid));

        var oid = await ExecuteScalarAsync<uint>(conn, "SELECT lo_create(0)");
        var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
            ("oid", NpgsqlDbType.Oid, oid), ("mode", NpgsqlDbType.Integer, InvWrite));

        var buffer = new byte[ChunkSize];
        int read;
        while ((read = await data.ReadAsync(buffer)) > 0)
        {
            var chunk = buffer[..read];
            await ExecuteScalarAsync<int>(conn, "SELECT lowrite(@fd, @chunk)",
                ("fd", NpgsqlDbType.Integer, fd), ("chunk", NpgsqlDbType.Bytea, chunk));
        }
        await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd));

        if (existing is not null)
            existing.Oid = oid;
        else
            db.DocumentData.Add(new DocumentData { DocumentId = documentId, Oid = oid });

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return documentId.ToString();
    }

    public async Task<byte[]> LoadAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return [];

        await using var tx = await db.Database.BeginTransactionAsync();
        var row = await db.DocumentData.FindAsync(id);
        if (row is null) return [];

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
            ("oid", NpgsqlDbType.Oid, row.Oid), ("mode", NpgsqlDbType.Integer, InvRead));

        using var ms = new MemoryStream();
        while (true)
        {
            var chunk = await ExecuteScalarAsync<byte[]>(conn, "SELECT loread(@fd, @len)",
                ("fd", NpgsqlDbType.Integer, fd), ("len", NpgsqlDbType.Integer, ChunkSize));
            if (chunk.Length > 0) await ms.WriteAsync(chunk);
            if (chunk.Length < ChunkSize) break;
        }
        await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd));
        await tx.CommitAsync();
        return ms.ToArray();
    }

    public async Task DeleteAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return;

        await using var tx = await db.Database.BeginTransactionAsync();
        var row = await db.DocumentData.FindAsync(id);
        if (row is null) return;

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await ExecuteScalarAsync<int>(conn, "SELECT lo_unlink(@oid)", ("oid", NpgsqlDbType.Oid, row.Oid));
        db.DocumentData.Remove(row);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static async Task<T> ExecuteScalarAsync<T>(NpgsqlConnection conn, string sql,
        params (string Name, NpgsqlDbType Type, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, type, value) in parameters)
            cmd.Parameters.Add(new NpgsqlParameter { ParameterName = name, NpgsqlDbType = type, Value = value });
        var result = await cmd.ExecuteScalarAsync();
        return (T)result!;
    }
}
