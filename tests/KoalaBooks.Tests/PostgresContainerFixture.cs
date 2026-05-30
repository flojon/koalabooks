using Npgsql;
using Testcontainers.PostgreSql;

namespace KoalaBooks.Tests;

/// <summary>
/// One Postgres container per test process, shared across all test classes.
/// Each caller gets its own database via CreateUniqueDatabase() so test classes
/// can run in parallel without interfering with each other.
/// </summary>
internal static class PostgresContainerFixture
{
    private static readonly PostgreSqlContainer _container = CreateAndStart();

    private static PostgreSqlContainer CreateAndStart()
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        container.StartAsync().GetAwaiter().GetResult();
        return container;
    }

    public static string ConnectionString => _container.GetConnectionString();

    public static (string dbName, string connStr) CreateUniqueDatabase()
    {
        var dbName = $"koalatest_{Guid.NewGuid():N}";
        using var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
        cmd.ExecuteNonQuery();
        var connStr = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = dbName }.ConnectionString;
        return (dbName, connStr);
    }

    public static void DropDatabase(string dbName)
    {
        using var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)";
        cmd.ExecuteNonQuery();
    }
}
