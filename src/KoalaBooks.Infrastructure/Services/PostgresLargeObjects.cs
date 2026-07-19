using Npgsql;
using NpgsqlTypes;

namespace KoalaBooks.Infrastructure.Services;

// Plain-SQL lo_* calls, not Npgsql's NpgsqlLargeObjectManager/Stream ([Obsolete] as of
// Npgsql 8.0 in favor of these). Callers own the transaction; sequential-only (no Seek)
// since both current callers only ever need a forward write or forward read.
public static class PostgresLargeObjects
{
    // https://www.postgresql.org/docs/current/lo-interfaces.html#LO-INTERFACES-OPEN
    private const int InvWrite = 0x00020000;
    private const int InvRead = 0x00040000;
    private const int ChunkSize = 81920; // matches Stream.CopyToAsync's default buffer size

    public static async Task<(uint Oid, long Length)> CopyStreamIntoNewLargeObjectAsync(NpgsqlConnection conn, Stream source)
    {
        var oid = await ExecuteScalarAsync<uint>(conn, "SELECT lo_create(0)").ConfigureAwait(false);
        var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
            ("oid", NpgsqlDbType.Oid, oid), ("mode", NpgsqlDbType.Integer, InvWrite)).ConfigureAwait(false);

        var buffer = new byte[ChunkSize];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            length += read;
            var chunk = buffer[..read];
            await ExecuteScalarAsync<int>(conn, "SELECT lowrite(@fd, @chunk)",
                ("fd", NpgsqlDbType.Integer, fd), ("chunk", NpgsqlDbType.Bytea, chunk)).ConfigureAwait(false);
        }
        await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd)).ConfigureAwait(false);

        return (oid, length);
    }

    public static async Task CopyLargeObjectIntoStreamAsync(NpgsqlConnection conn, uint oid, Stream destination)
    {
        var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
            ("oid", NpgsqlDbType.Oid, oid), ("mode", NpgsqlDbType.Integer, InvRead)).ConfigureAwait(false);

        while (true)
        {
            var chunk = await ExecuteScalarAsync<byte[]>(conn, "SELECT loread(@fd, @len)",
                ("fd", NpgsqlDbType.Integer, fd), ("len", NpgsqlDbType.Integer, ChunkSize)).ConfigureAwait(false);
            if (chunk.Length > 0) await destination.WriteAsync(chunk).ConfigureAwait(false);
            if (chunk.Length < ChunkSize) break;
        }
        await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd)).ConfigureAwait(false);
    }

    public static Task DeleteLargeObjectAsync(NpgsqlConnection conn, uint oid) =>
        ExecuteScalarAsync<int>(conn, "SELECT lo_unlink(@oid)", ("oid", NpgsqlDbType.Oid, oid));

    private static async Task<T> ExecuteScalarAsync<T>(NpgsqlConnection conn, string sql,
        params (string Name, NpgsqlDbType Type, object Value)[] parameters)
    {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
        await using var cmd = new NpgsqlCommand(sql, conn);
#pragma warning restore CA2007
        foreach (var (name, type, value) in parameters)
            cmd.Parameters.Add(new NpgsqlParameter { ParameterName = name, NpgsqlDbType = type, Value = value });
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return (T)result!;
    }
}
