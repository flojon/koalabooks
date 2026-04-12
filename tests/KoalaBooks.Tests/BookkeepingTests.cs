using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class CsvImportServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly CsvImportService _service;
    private readonly FiscalYear _fiscalYear;

    public CsvImportServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _service = new CsvImportService(_db);

        _fiscalYear = new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        };
        _db.FiscalYears.Add(_fiscalYear);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ImportAccounts_CreatesNewAccounts()
    {
        var csv = "AccountNumber,Name\n1910,Kassa\n3010,Försäljning\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        var result = await _service.ImportAccountsAsync(stream, _fiscalYear.Id);

        Assert.Equal(2, result.Created);
        Assert.Equal(0, result.Updated);
        var accounts = await _db.Accounts.OrderBy(a => a.AccountNumber).ToListAsync();
        Assert.Equal(2, accounts.Count);
        Assert.Equal("1910", accounts[0].AccountNumber);
        Assert.Equal(AccountClass.Asset, accounts[0].AccountClass);
        Assert.Equal("3010", accounts[1].AccountNumber);
        Assert.Equal(AccountClass.Revenue, accounts[1].AccountClass);
    }

    [Fact]
    public async Task ImportAccounts_UpdatesExistingAccount()
    {
        _db.Accounts.Add(new Account { AccountNumber = "1910", Name = "Old Name", AccountClass = AccountClass.Asset, FiscalYearId = _fiscalYear.Id });
        await _db.SaveChangesAsync();

        var csv = "AccountNumber,Name\n1910,Kassa\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        var result = await _service.ImportAccountsAsync(stream, _fiscalYear.Id);

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal("Kassa", (await _db.Accounts.SingleAsync()).Name);
    }

    [Fact]
    public async Task ImportAccounts_SkipsEmptyRows()
    {
        var csv = "AccountNumber,Name\n,\n1910,Kassa\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        var result = await _service.ImportAccountsAsync(stream, _fiscalYear.Id);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task ImportAccounts_MapsAccountClassesCorrectly()
    {
        var csv = "AccountNumber,Name\n1000,Tillgångar\n2000,Skulder\n3000,Intäkter\n4000,Kostnader\n5000,Övriga kostnader\n8000,Eget kapital\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        var result = await _service.ImportAccountsAsync(stream, _fiscalYear.Id);

        Assert.Equal(6, result.Created);
        var accounts = await _db.Accounts.OrderBy(a => a.AccountNumber).ToListAsync();
        Assert.Equal(AccountClass.Asset, accounts[0].AccountClass);
        Assert.Equal(AccountClass.Liability, accounts[1].AccountClass);
        Assert.Equal(AccountClass.Revenue, accounts[2].AccountClass);
        Assert.Equal(AccountClass.Expense, accounts[3].AccountClass);
        Assert.Equal(AccountClass.Expense, accounts[4].AccountClass);
        Assert.Equal(AccountClass.Equity, accounts[5].AccountClass);
    }
}

public class JournalEntryServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly JournalEntryService _service;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _account1;
    private readonly Account _account2;

    public JournalEntryServiceTests()
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

        _account1 = new Account { AccountNumber = "1910", Name = "Kassa", AccountClass = AccountClass.Asset, FiscalYearId = _fiscalYear.Id };
        _account2 = new Account { AccountNumber = "3010", Name = "Försäljning", AccountClass = AccountClass.Revenue, FiscalYearId = _fiscalYear.Id };
        _db.Accounts.AddRange(_account1, _account2);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateEntry_BalancedEntry_Succeeds()
    {
        var entry = MakeEntry(1000m);
        var (result, error) = await _service.CreateAsync(entry);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(1, result.EntryNumber);
    }

    [Fact]
    public async Task CreateEntry_UnbalancedEntry_Fails()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 3, 1),
            Description = "Unbalanced",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = 1000, CreditAmount = 0 },
                new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = 500 }
            ]
        };

        var (result, error) = await _service.CreateAsync(entry);

        Assert.NotNull(error);
        Assert.Contains("Debit", error);
        Assert.Null(result);
    }

    [Fact]
    public async Task CreateEntry_LessThanTwoLines_Fails()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 3, 1),
            Description = "One line",
            FiscalYearId = _fiscalYear.Id,
            Lines = [new() { AccountId = _account1.Id, DebitAmount = 100, CreditAmount = 0 }]
        };

        var (_, error) = await _service.CreateAsync(entry);
        Assert.Contains("at least 2 lines", error);
    }

    [Fact]
    public async Task CreateEntry_ClosedFiscalYear_Fails()
    {
        _fiscalYear.IsClosed = true;
        await _db.SaveChangesAsync();

        var entry = MakeEntry(500m);
        var (_, error) = await _service.CreateAsync(entry);
        Assert.Contains("closed", error);
    }

    [Fact]
    public async Task CreateEntry_SequentialEntryNumbers()
    {
        var (e1, _) = await _service.CreateAsync(MakeEntry(100));
        var (e2, _) = await _service.CreateAsync(MakeEntry(200));

        Assert.Equal(1, e1!.EntryNumber);
        Assert.Equal(2, e2!.EntryNumber);
    }

    [Fact]
    public async Task CreateEntry_NegativeAmount_Fails()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 3, 1),
            Description = "Negative",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = -100, CreditAmount = 0 },
                new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = -100 }
            ]
        };

        var (_, error) = await _service.CreateAsync(entry);
        Assert.Contains("negative", error);
    }

    [Fact]
    public async Task CreateEntry_BothDebitAndCredit_Fails()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 3, 1),
            Description = "Both",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = 100, CreditAmount = 50 },
                new() { AccountId = _account2.Id, DebitAmount = 50, CreditAmount = 100 }
            ]
        };

        var (_, error) = await _service.CreateAsync(entry);
        Assert.Contains("both debit and credit", error);
    }

    [Fact]
    public async Task GetTrialBalance_ReturnsCorrectTotals()
    {
        await _service.CreateAsync(MakeEntry(1000));
        await _service.CreateAsync(MakeEntry(500));

        var rows = await _service.GetTrialBalanceAsync(_fiscalYear.Id);

        Assert.Equal(2, rows.Count);
        var debitAccount = rows.Single(r => r.AccountNumber == "1910");
        Assert.Equal(1500, debitAccount.TotalDebit);
        Assert.Equal(0, debitAccount.TotalCredit);
    }

    private JournalEntry MakeEntry(decimal amount) => new()
    {
        Date = new DateOnly(2026, 3, 1),
        Description = $"Test entry {amount}",
        FiscalYearId = _fiscalYear.Id,
        Lines =
        [
            new() { AccountId = _account1.Id, DebitAmount = amount, CreditAmount = 0 },
            new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = amount }
        ]
    };
}
