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
    private const string AppUserPassword = "test-app-user-password";

    private static readonly PostgreSqlContainer _container = CreateAndStart();

    private static PostgreSqlContainer CreateAndStart()
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        container.StartAsync().GetAwaiter().GetResult();
        CreateAppUserRole(container);
        return container;
    }

    private static void CreateAppUserRole(PostgreSqlContainer container)
    {
        using var conn = new NpgsqlConnection(container.GetConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'app_user') THEN
                    CREATE ROLE app_user LOGIN PASSWORD '{AppUserPassword}';
                END IF;
            END
            $$;
            """;
        cmd.ExecuteNonQuery();
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

        GrantAppUserOnDatabase(dbName);

        var connStr = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = dbName }.ConnectionString;
        return (dbName, connStr);
    }

    private static void GrantAppUserOnDatabase(string dbName)
    {
        var connStrForNewDb = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = dbName }.ConnectionString;
        using var conn = new NpgsqlConnection(connStrForNewDb);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            GRANT USAGE ON SCHEMA public TO app_user;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO app_user;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_user;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO app_user;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Non-superuser connection to a database created via CreateUniqueDatabase(), distinct
    /// from the superuser connection used for schema setup. Lets row-level-security tests
    /// verify enforcement actually happens instead of being silently bypassed by a superuser
    /// session.
    /// </summary>
    public static string CreateAppUserConnectionString(string dbName)
    {
        return new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = dbName,
            Username = "app_user",
            Password = AppUserPassword
        }.ConnectionString;
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
