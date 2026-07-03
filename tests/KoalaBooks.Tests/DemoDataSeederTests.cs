using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
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

    [Fact]
    public async Task SeedAsync_LeavesOneVoucherGapInCurrentYear()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var currentFiscalYearId = await db.FiscalYears
                .Where(f => !f.IsClosed)
                .Select(f => f.Id)
                .SingleAsync();

            var entryNumbers = await db.JournalEntries
                .Where(j => j.FiscalYearId == currentFiscalYearId)
                .OrderBy(j => j.EntryNumber)
                .Select(j => j.EntryNumber)
                .ToListAsync();
            Assert.Equal([1, 2, 4, 5, 6], entryNumbers);
        }
    }

    [Fact]
    public async Task SeedAsync_SpreadsCurrentYearEntriesAcrossMonths()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var currentFiscalYearId = await db.FiscalYears
                .Where(f => !f.IsClosed)
                .Select(f => f.Id)
                .SingleAsync();

            var months = await db.JournalEntries
                .Where(j => j.FiscalYearId == currentFiscalYearId)
                .Select(j => j.Date.Month)
                .Distinct()
                .ToListAsync();
            Assert.True(months.Count >= 5, $"Expected entries spread across at least 5 distinct months, got {months.Count}.");
        }
    }

    [Fact]
    public async Task SeedAsync_ClosesPreviousYearWithFourEntries()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var previousFiscalYear = await db.FiscalYears
                .Where(f => f.IsClosed)
                .SingleAsync();
            Assert.NotNull(previousFiscalYear.ClosedAt);

            var operatingEntryCount = await db.JournalEntries
                .CountAsync(j => j.FiscalYearId == previousFiscalYear.Id && !j.IsClosingEntry);
            Assert.Equal(4, operatingEntryCount);

            // Closed via YearEndClosingService, so it also posts the standard P&L-to-8999
            // and 8999-to-2099 closing entries.
            var closingEntryCount = await db.JournalEntries
                .CountAsync(j => j.FiscalYearId == previousFiscalYear.Id && j.IsClosingEntry);
            Assert.Equal(2, closingEntryCount);
        }
    }

    [Fact]
    public async Task SeedAsync_CarriesPreviousYearClosingBalanceIntoCurrentYear()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var previousFiscalYear = await db.FiscalYears.Where(f => f.IsClosed).SingleAsync();
            var currentFiscalYear = await db.FiscalYears.Where(f => !f.IsClosed).SingleAsync();
            Assert.Equal(previousFiscalYear.Id, currentFiscalYear.PreviousFiscalYearId);

            var previousCash = await db.Accounts
                .SingleAsync(a => a.FiscalYearId == previousFiscalYear.Id && a.AccountNumber == "1910");
            var currentCash = await db.Accounts
                .SingleAsync(a => a.FiscalYearId == currentFiscalYear.Id && a.AccountNumber == "1910");

            Assert.NotEqual(0, previousCash.OutgoingBalance);
            Assert.Equal(previousCash.OutgoingBalance, currentCash.IncomingBalance);
        }
    }

    [Fact]
    public async Task SeedAsync_DoesNotDuplicateFiscalYearsOnRetryAfterPartialFailure()
    {
        using (var scope = _sp.CreateScope())
            await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        // Simulate the exact retry scenario a crash mid-seed produces: the organisation and
        // its books were committed, but the demo user (the idempotency marker) wasn't.
        using (var scope = _sp.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(DemoDataSeeder.DemoUserEmail);
            await userManager.DeleteAsync(user!);
        }

        using (var scope = _sp.CreateScope())
            await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        using var verifyScope = _sp.CreateScope();
        var (db, _) = await OpenTenantDbAsync(verifyScope.ServiceProvider);
        await using (db)
        {
            Assert.Equal(2, await db.FiscalYears.CountAsync());
        }
    }

    public void Dispose()
    {
        _sp.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }
}
