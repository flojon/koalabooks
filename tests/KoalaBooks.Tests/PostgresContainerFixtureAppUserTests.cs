using Npgsql;
using Xunit;

namespace KoalaBooks.Tests;

public class PostgresContainerFixtureAppUserTests
{
    [Fact]
    public void AppUserConnection_IsNotSuperuser()
    {
        var (dbName, _) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            var appConnStr = PostgresContainerFixture.CreateAppUserConnectionString(dbName);

            using var conn = new NpgsqlConnection(appConnStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT rolsuper FROM pg_roles WHERE rolname = current_user;";
            var isSuperuser = (bool)cmd.ExecuteScalar()!;

            Assert.False(isSuperuser);
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }

    [Fact]
    public void AppUserConnection_CanReadWriteTablesCreatedByMigratorRole()
    {
        var (dbName, migratorConnStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            using (var migratorConn = new NpgsqlConnection(migratorConnStr))
            {
                migratorConn.Open();
                using var createCmd = migratorConn.CreateCommand();
                createCmd.CommandText = "CREATE TABLE role_sep_probe (id serial primary key, value text);";
                createCmd.ExecuteNonQuery();
            }

            var appConnStr = PostgresContainerFixture.CreateAppUserConnectionString(dbName);
            using var appConn = new NpgsqlConnection(appConnStr);
            appConn.Open();

            using var insertCmd = appConn.CreateCommand();
            insertCmd.CommandText = "INSERT INTO role_sep_probe (value) VALUES ('ok');";
            insertCmd.ExecuteNonQuery();

            using var selectCmd = appConn.CreateCommand();
            selectCmd.CommandText = "SELECT value FROM role_sep_probe;";
            var value = (string)selectCmd.ExecuteScalar()!;

            Assert.Equal("ok", value);
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }
}
