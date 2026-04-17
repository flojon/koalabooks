using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

/// <summary>
/// P0 #7: Date and account validation tests.
/// Current bug: CreateAsync does not validate that entry dates fall within the
/// fiscal year range or that referenced account IDs exist.
/// </summary>
public class DateAccountValidationTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly JournalEntryService _service;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _account1;
    private readonly Account _account2;

    public DateAccountValidationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _service = new JournalEntryService(_db);

        _fiscalYear = new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        };
        _db.FiscalYears.Add(_fiscalYear);
        _db.SaveChanges();

        _account1 = new Account
        {
            AccountNumber = "1910",
            Name = "Kassa",
            AccountClass = AccountClass.Asset,
            FiscalYearId = _fiscalYear.Id
        };
        _account2 = new Account
        {
            AccountNumber = "3010",
            Name = "Försäljning",
            AccountClass = AccountClass.Revenue,
            FiscalYearId = _fiscalYear.Id
        };
        _db.Accounts.AddRange(_account1, _account2);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateEntry_DateBeforeFiscalYearStart_Fails()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2025, 12, 31), // Before fiscal year 2026
            Description = "Too early",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = 1000, CreditAmount = 0 },
                new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = 1000 }
            ]
        };

        var (result, error) = await _service.CreateAsync(entry);

        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task CreateEntry_DateAfterFiscalYearEnd_Fails()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2027, 1, 1), // After fiscal year 2026
            Description = "Too late",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = 1000, CreditAmount = 0 },
                new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = 1000 }
            ]
        };

        var (result, error) = await _service.CreateAsync(entry);

        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task CreateEntry_DateOnFiscalYearStartDate_Succeeds()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 1, 1), // First day of fiscal year
            Description = "First day",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = 500, CreditAmount = 0 },
                new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = 500 }
            ]
        };

        var (result, error) = await _service.CreateAsync(entry);

        Assert.Null(error);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateEntry_DateOnFiscalYearEndDate_Succeeds()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 12, 31), // Last day of fiscal year
            Description = "Last day",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = 500, CreditAmount = 0 },
                new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = 500 }
            ]
        };

        var (result, error) = await _service.CreateAsync(entry);

        Assert.Null(error);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateEntry_DateInMiddleOfFiscalYear_Succeeds()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 6, 15),
            Description = "Mid-year",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = 1000, CreditAmount = 0 },
                new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = 1000 }
            ]
        };

        var (result, error) = await _service.CreateAsync(entry);

        Assert.Null(error);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateEntry_NonExistentAccountId_Fails()
    {
        var nonExistentAccountId = 99999;
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 6, 1),
            Description = "Bad account",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = nonExistentAccountId, DebitAmount = 1000, CreditAmount = 0 },
                new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = 1000 }
            ]
        };

        var (result, error) = await _service.CreateAsync(entry);

        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task CreateEntry_AllNonExistentAccounts_Fails()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 6, 1),
            Description = "All bad accounts",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = 99998, DebitAmount = 1000, CreditAmount = 0 },
                new() { AccountId = 99999, DebitAmount = 0, CreditAmount = 1000 }
            ]
        };

        var (result, error) = await _service.CreateAsync(entry);

        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task CreateEntry_ValidDateAndAccounts_Succeeds()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 3, 15),
            Description = "All valid",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = 1000, CreditAmount = 0 },
                new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = 1000 }
            ]
        };

        var (result, error) = await _service.CreateAsync(entry);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(1, result.EntryNumber);
    }
}
