using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class GeneralLedgerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly JournalEntryService _service;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _cashAccount;
    private readonly Account _revenueAccount;
    private readonly Account _expenseAccount;

    public GeneralLedgerTests()
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

        _cashAccount = new Account
        {
            AccountNumber = "1910",
            Name = "Kassa",
            AccountClass = AccountClass.Asset,
            IncomingBalance = 5000m,
            FiscalYearId = _fiscalYear.Id
        };
        _revenueAccount = new Account
        {
            AccountNumber = "3010",
            Name = "Försäljning",
            AccountClass = AccountClass.Revenue,
            IncomingBalance = 0m,
            FiscalYearId = _fiscalYear.Id
        };
        _expenseAccount = new Account
        {
            AccountNumber = "5010",
            Name = "Lokalhyra",
            AccountClass = AccountClass.Expense,
            IncomingBalance = 0m,
            FiscalYearId = _fiscalYear.Id
        };
        _db.Accounts.AddRange(_cashAccount, _revenueAccount, _expenseAccount);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GeneralLedger_WithIBAndTransactions_ReturnsCorrectSections()
    {
        await CreateEntry(new DateOnly(2026, 1, 15), "Sale 1", _cashAccount.Id, _revenueAccount.Id, 1000m);
        await CreateEntry(new DateOnly(2026, 2, 10), "Sale 2", _cashAccount.Id, _revenueAccount.Id, 2000m);

        var sections = await _service.GetGeneralLedgerAsync(_fiscalYear.Id);

        Assert.Equal(3, sections.Count);

        var cash = sections.Single(s => s.AccountNumber == "1910");
        Assert.Equal(5000m, cash.IncomingBalance);
        Assert.Equal(2, cash.Rows.Count);
        Assert.Equal(6000m, cash.Rows[0].RunningBalance); // 5000 + 1000
        Assert.Equal(8000m, cash.Rows[1].RunningBalance); // 6000 + 2000
        Assert.Equal(8000m, cash.ClosingBalance);

        var revenue = sections.Single(s => s.AccountNumber == "3010");
        Assert.Equal(0m, revenue.IncomingBalance);
        Assert.Equal(2, revenue.Rows.Count);
        Assert.Equal(-1000m, revenue.Rows[0].RunningBalance); // 0 - 1000
        Assert.Equal(-3000m, revenue.Rows[1].RunningBalance); // -1000 - 2000
        Assert.Equal(-3000m, revenue.ClosingBalance);
    }

    [Fact]
    public async Task GeneralLedger_AccountRangeFilter_ReturnsOnlyMatchingAccounts()
    {
        await CreateEntry(new DateOnly(2026, 3, 1), "Sale", _cashAccount.Id, _revenueAccount.Id, 500m);

        var sections = await _service.GetGeneralLedgerAsync(_fiscalYear.Id, fromAccount: "1000", toAccount: "1999");

        Assert.Single(sections);
        Assert.Equal("1910", sections[0].AccountNumber);
    }

    [Fact]
    public async Task GeneralLedger_DateRangeFilter_ReturnsOnlyMatchingTransactions()
    {
        await CreateEntry(new DateOnly(2026, 1, 10), "Jan sale", _cashAccount.Id, _revenueAccount.Id, 1000m);
        await CreateEntry(new DateOnly(2026, 3, 15), "Mar sale", _cashAccount.Id, _revenueAccount.Id, 2000m);
        await CreateEntry(new DateOnly(2026, 6, 20), "Jun sale", _cashAccount.Id, _revenueAccount.Id, 3000m);

        var sections = await _service.GetGeneralLedgerAsync(
            _fiscalYear.Id, from: new DateOnly(2026, 2, 1), to: new DateOnly(2026, 4, 30));

        var cash = sections.Single(s => s.AccountNumber == "1910");
        Assert.Single(cash.Rows);
        Assert.Equal("Mar sale", cash.Rows[0].Description);
        // Running balance starts from IB (5000) + the filtered transaction
        Assert.Equal(7000m, cash.Rows[0].RunningBalance); // 5000 + 2000
        Assert.Equal(7000m, cash.ClosingBalance);
    }

    [Fact]
    public async Task GeneralLedger_RunningBalance_CalculatesCorrectly()
    {
        await CreateEntry(new DateOnly(2026, 1, 5), "Entry A", _cashAccount.Id, _revenueAccount.Id, 100m);
        await CreateEntry(new DateOnly(2026, 1, 10), "Entry B", _expenseAccount.Id, _cashAccount.Id, 50m);
        await CreateEntry(new DateOnly(2026, 1, 15), "Entry C", _cashAccount.Id, _revenueAccount.Id, 200m);

        var sections = await _service.GetGeneralLedgerAsync(_fiscalYear.Id);

        var cash = sections.Single(s => s.AccountNumber == "1910");
        Assert.Equal(3, cash.Rows.Count);
        Assert.Equal(5100m, cash.Rows[0].RunningBalance); // 5000 + 100 debit
        Assert.Equal(5050m, cash.Rows[1].RunningBalance); // 5100 - 50 credit
        Assert.Equal(5250m, cash.Rows[2].RunningBalance); // 5050 + 200 debit
        Assert.Equal(5250m, cash.ClosingBalance);
    }

    [Fact]
    public async Task GeneralLedger_EmptyAccount_ShowsOnlyIB()
    {
        // No transactions — expense account has IB = 0, cash has IB = 5000
        var sections = await _service.GetGeneralLedgerAsync(_fiscalYear.Id);

        var cash = sections.Single(s => s.AccountNumber == "1910");
        Assert.Empty(cash.Rows);
        Assert.Equal(5000m, cash.IncomingBalance);
        Assert.Equal(5000m, cash.ClosingBalance);

        var expense = sections.Single(s => s.AccountNumber == "5010");
        Assert.Empty(expense.Rows);
        Assert.Equal(0m, expense.IncomingBalance);
        Assert.Equal(0m, expense.ClosingBalance);
    }

    private async Task CreateEntry(DateOnly date, string description, int debitAccountId, int creditAccountId, decimal amount)
    {
        var entry = new JournalEntry
        {
            Date = date,
            Description = description,
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = debitAccountId, DebitAmount = amount, CreditAmount = 0 },
                new() { AccountId = creditAccountId, DebitAmount = 0, CreditAmount = amount }
            ]
        };
        await _service.CreateAsync(entry);
    }
}
