// src/KoalaBooks.Infrastructure/Services/PostgresLargeObjects.cs
using Npgsql;
using NpgsqlTypes;

namespace KoalaBooks.Infrastructure.Services;

// Plain-SQL lo_* function calls, matching DbDocumentStorage's existing approach —
// deliberately not Npgsql's NpgsqlLargeObjectManager/NpgsqlLargeObjectStream, which
// are [Obsolete] as of Npgsql 8.0 specifically in favor of calling these functions
// directly. Callers own the surrounding transaction; these are sequential-only
// (no Seek) — that's sufficient for both current callers, which each need either a
// forward write or a forward read, never random access.
public static class PostgresLargeObjects
{
    // https://www.postgresql.org/docs/current/lo-interfaces.html#LO-INTERFACES-OPEN
    private const int InvWrite = 0x00020000;
    private const int InvRead = 0x00040000;
    private const int ChunkSize = 81920; // matches Stream.CopyToAsync's default buffer size

    public static async Task<(uint Oid, long Length)> CopyStreamIntoNewLargeObjectAsync(NpgsqlConnection conn, Stream source)
    {
        var oid = await ExecuteScalarAsync<uint>(conn, "SELECT lo_create(0)");
        var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
            ("oid", NpgsqlDbType.Oid, oid), ("mode", NpgsqlDbType.Integer, InvWrite));

        var buffer = new byte[ChunkSize];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            length += read;
            var chunk = buffer[..read];
            await ExecuteScalarAsync<int>(conn, "SELECT lowrite(@fd, @chunk)",
                ("fd", NpgsqlDbType.Integer, fd), ("chunk", NpgsqlDbType.Bytea, chunk));
        }
        await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd));

        return (oid, length);
    }

    public static async Task CopyLargeObjectIntoStreamAsync(NpgsqlConnection conn, uint oid, Stream destination)
    {
        var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
            ("oid", NpgsqlDbType.Oid, oid), ("mode", NpgsqlDbType.Integer, InvRead));

        while (true)
        {
            var chunk = await ExecuteScalarAsync<byte[]>(conn, "SELECT loread(@fd, @len)",
                ("fd", NpgsqlDbType.Integer, fd), ("len", NpgsqlDbType.Integer, ChunkSize));
            if (chunk.Length > 0) await destination.WriteAsync(chunk);
            if (chunk.Length < ChunkSize) break;
        }
        await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd));
    }

    public static Task DeleteLargeObjectAsync(NpgsqlConnection conn, uint oid) =>
        ExecuteScalarAsync<int>(conn, "SELECT lo_unlink(@oid)", ("oid", NpgsqlDbType.Oid, oid));

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
