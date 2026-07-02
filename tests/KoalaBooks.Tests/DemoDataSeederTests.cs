using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Tests;

public class DemoDataSeederTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbName;

    public DemoDataSeederTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUser>(new LocalCurrentUser());
        services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(connStr));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<AppDbContext>();

        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    /// <summary>
    /// The DI-registered AppDbContext is tied to a singleton LocalCurrentUser with
    /// OrganisationId = null, so it can't see tenant-scoped rows. Organisations has no
    /// tenant filter, so we read the seeded org id from it, then open a second
    /// AppDbContext scoped to that org for verifying tenant-scoped data.
    /// </summary>
    private async Task<(AppDbContext Db, int OrganisationId)> OpenTenantDbAsync(IServiceProvider services)
    {
        var options = services.GetRequiredService<DbContextOptions<AppDbContext>>();
        await using var untenanted = new AppDbContext(options, new LocalCurrentUser());
        var orgId = await untenanted.Organisations.Select(o => o.Id).SingleAsync();
        return (new AppDbContext(options, new LocalCurrentUser(orgId)), orgId);
    }

    [Fact]
    public async Task SeedAsync_CreatesLoginableDemoUser()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(DemoDataSeeder.DemoUserEmail);

        Assert.NotNull(user);
        Assert.True(await userManager.CheckPasswordAsync(user!, DemoDataSeeder.DemoUserPassword));
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent()
    {
        using (var scope = _sp.CreateScope())
            await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        using (var scope = _sp.CreateScope())
            await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        using var verifyScope = _sp.CreateScope();
        var (db, _) = await OpenTenantDbAsync(verifyScope.ServiceProvider);
        await using (db)
        {
            var options = verifyScope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
            await using var untenanted = new AppDbContext(options, new LocalCurrentUser());
            Assert.Equal(1, await untenanted.Organisations.CountAsync());
        }
    }

    [Fact]
    public async Task SeedAsync_CreatesTwoFiscalYears()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var years = await db.FiscalYears.OrderBy(f => f.Name).ToListAsync();
            Assert.Equal(2, years.Count);

            var currentYear = DateTime.UtcNow.Year;
            Assert.Equal((currentYear - 1).ToString(), years[0].Name);
            Assert.Equal(currentYear.ToString(), years[1].Name);
        }
    }

    [Fact]
    public async Task SeedAsync_ImportsBasChartOfAccounts()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var fiscalYearIds = await db.FiscalYears.Select(f => f.Id).ToListAsync();
            Assert.Equal(2, fiscalYearIds.Count);

            foreach (var fiscalYearId in fiscalYearIds)
            {
                var accountNumbers = await db.Accounts
                    .Where(a => a.FiscalYearId == fiscalYearId)
                    .Select(a => a.AccountNumber)
                    .ToListAsync();
                Assert.True(accountNumbers.Count > 1000,
                    $"Expected a full BAS import for fiscal year {fiscalYearId}, got {accountNumbers.Count} accounts.");
                foreach (var expected in new[] { "1910", "2440", "2081", "3001", "5010" })
                    Assert.Contains(expected, accountNumbers);
            }
        }
    }

    public void Dispose()
    {
        _sp.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }
}
