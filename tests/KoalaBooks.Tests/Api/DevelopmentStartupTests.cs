using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Tests;

// Pooled AppDbContext used to crash resolving its scoped ICurrentUser dependency
// whenever DI scope validation was enabled (the Development default).
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
