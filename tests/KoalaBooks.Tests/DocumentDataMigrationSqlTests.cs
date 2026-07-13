using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace KoalaBooks.Tests;

public class DocumentDataMigrationSqlTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task LoFromBytea_ThenLoGet_RoundTripsExactBytes()
    {
        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        var original = new byte[] { 10, 20, 30, 40, 250 };

        await using var tx = await conn.BeginTransactionAsync();

        uint oid;
        await using (var createCmd = new NpgsqlCommand(@"SELECT lo_from_bytea(0, @data)", conn))
        {
            createCmd.Parameters.Add(new NpgsqlParameter { ParameterName = "data", NpgsqlDbType = NpgsqlDbType.Bytea, Value = original });
            oid = (uint)(await createCmd.ExecuteScalarAsync())!;
        }

        byte[] roundTripped;
        await using (var readCmd = new NpgsqlCommand(@"SELECT lo_get(@oid)", conn))
        {
            readCmd.Parameters.Add(new NpgsqlParameter { ParameterName = "oid", NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
            roundTripped = (byte[])(await readCmd.ExecuteScalarAsync())!;
        }

        await tx.CommitAsync();

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public async Task LoFromBytea_ThenLoGet_RoundTripsEmptyBytea()
    {
        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        await using var tx = await conn.BeginTransactionAsync();

        uint oid;
        await using (var createCmd = new NpgsqlCommand(@"SELECT lo_from_bytea(0, @data)", conn))
        {
            createCmd.Parameters.Add(new NpgsqlParameter { ParameterName = "data", NpgsqlDbType = NpgsqlDbType.Bytea, Value = Array.Empty<byte>() });
            oid = (uint)(await createCmd.ExecuteScalarAsync())!;
        }

        byte[] roundTripped;
        await using (var readCmd = new NpgsqlCommand(@"SELECT lo_get(@oid)", conn))
        {
            readCmd.Parameters.Add(new NpgsqlParameter { ParameterName = "oid", NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
            roundTripped = (byte[])(await readCmd.ExecuteScalarAsync())!;
        }

        await tx.CommitAsync();

        Assert.Empty(roundTripped);
    }
}
