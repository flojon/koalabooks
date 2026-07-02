using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Tests;

// Regression test for #179: Development-mode startup (ASPNETCORE_ENVIRONMENT=Development,
// which enables DI scope validation) used to crash resolving AppDbContext because the
// context was registered pooled via AddNpgsqlDbContext, and the pool's activator resolves
// constructor dependencies (ICurrentUser, scoped) against the root provider, not a scope.
public class DevelopmentStartupTests
{
    private class DevelopmentWebApiFactory(string connStr) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:koalabooks", connStr);
            builder.UseDefaultServiceProvider(options => options.ValidateScopes = true);
        }
    }

    [Fact]
    public async Task Host_StartsAndResolvesAppDbContext_WithScopeValidationEnabled()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            await using var factory = new DevelopmentWebApiFactory(connStr);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.NotNull(db);
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }
}
