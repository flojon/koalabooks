using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class IncomeStatementTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly JournalEntryService _service;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _cashAccount;
    private readonly Account _revenueAccount1;
    private readonly Account _revenueAccount2;
    private readonly Account _expenseAccount1;
    private readonly Account _expenseAccount2;

    public IncomeStatementTests()
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
            IncomingBalance = 10000m,
            FiscalYearId = _fiscalYear.Id
        };
        _revenueAccount1 = new Account
        {
            AccountNumber = "3010",
            Name = "Försäljning varor",
            AccountClass = AccountClass.Revenue,
            FiscalYearId = _fiscalYear.Id
        };
        _revenueAccount2 = new Account
        {
            AccountNumber = "3020",
            Name = "Försäljning tjänster",
            AccountClass = AccountClass.Revenue,
            FiscalYearId = _fiscalYear.Id
        };
        _expenseAccount1 = new Account
        {
            AccountNumber = "5010",
            Name = "Lokalhyra",
            AccountClass = AccountClass.Expense,
            FiscalYearId = _fiscalYear.Id
        };
        _expenseAccount2 = new Account
        {
            AccountNumber = "6010",
            Name = "Kontorsmaterial",
            AccountClass = AccountClass.Expense,
            FiscalYearId = _fiscalYear.Id
        };
        _db.Accounts.AddRange(_cashAccount, _revenueAccount1, _revenueAccount2, _expenseAccount1, _expenseAccount2);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task RevenueAndExpenseAmounts_CalculatedCorrectly()
    {
        // Revenue: credit cash, credit revenue (cash debit, revenue credit)
        await CreateEntry(new DateOnly(2026, 2, 1), "Sale goods", _cashAccount.Id, _revenueAccount1.Id, 5000m);
        await CreateEntry(new DateOnly(2026, 3, 1), "Sale services", _cashAccount.Id, _revenueAccount2.Id, 3000m);
        // Expense: debit expense, credit cash
        await CreateEntry(new DateOnly(2026, 4, 1), "Rent", _expenseAccount1.Id, _cashAccount.Id, 2000m);
        await CreateEntry(new DateOnly(2026, 5, 1), "Supplies", _expenseAccount2.Id, _cashAccount.Id, 500m);

        var (sections, _) = await _service.GetIncomeStatementAsync(_fiscalYear.Id);

        var revenue = sections.Single(s => s.Title == "Intäkter");
        Assert.Equal(2, revenue.Rows.Count);
        Assert.Equal(5000m, revenue.Rows.Single(r => r.AccountNumber == "3010").Amount);
        Assert.Equal(3000m, revenue.Rows.Single(r => r.AccountNumber == "3020").Amount);
        Assert.Equal(8000m, revenue.Total);

        var expenses = sections.Single(s => s.Title == "Kostnader");
        Assert.Equal(2, expenses.Rows.Count);
        Assert.Equal(2000m, expenses.Rows.Single(r => r.AccountNumber == "5010").Amount);
        Assert.Equal(500m, expenses.Rows.Single(r => r.AccountNumber == "6010").Amount);
        Assert.Equal(2500m, expenses.Total);
    }

    [Fact]
    public async Task DateRangeFilter_ReturnsOnlyMatchingTransactions()
    {
        await CreateEntry(new DateOnly(2026, 1, 15), "Jan sale", _cashAccount.Id, _revenueAccount1.Id, 1000m);
        await CreateEntry(new DateOnly(2026, 3, 15), "Mar sale", _cashAccount.Id, _revenueAccount1.Id, 2000m);
        await CreateEntry(new DateOnly(2026, 6, 15), "Jun sale", _cashAccount.Id, _revenueAccount1.Id, 4000m);
        await CreateEntry(new DateOnly(2026, 4, 1), "Rent Q1", _expenseAccount1.Id, _cashAccount.Id, 500m);

        var (sections, netResult) = await _service.GetIncomeStatementAsync(
            _fiscalYear.Id, from: new DateOnly(2026, 2, 1), to: new DateOnly(2026, 4, 30));

        var revenue = sections.Single(s => s.Title == "Intäkter");
        Assert.Single(revenue.Rows);
        Assert.Equal(2000m, revenue.Rows[0].Amount);
        Assert.Equal(2000m, revenue.Total);

        var expenses = sections.Single(s => s.Title == "Kostnader");
        Assert.Single(expenses.Rows);
        Assert.Equal(500m, expenses.Rows[0].Amount);
        Assert.Equal(500m, expenses.Total);

        Assert.Equal(1500m, netResult);
    }

    [Fact]
    public async Task NetResult_ProfitScenario_ReturnsPositiveValue()
    {
        await CreateEntry(new DateOnly(2026, 1, 10), "Sale", _cashAccount.Id, _revenueAccount1.Id, 10000m);
        await CreateEntry(new DateOnly(2026, 2, 10), "Rent", _expenseAccount1.Id, _cashAccount.Id, 3000m);
        await CreateEntry(new DateOnly(2026, 3, 10), "Supplies", _expenseAccount2.Id, _cashAccount.Id, 1000m);

        var (sections, netResult) = await _service.GetIncomeStatementAsync(_fiscalYear.Id);

        Assert.Equal(10000m, sections.Single(s => s.Title == "Intäkter").Total);
        Assert.Equal(4000m, sections.Single(s => s.Title == "Kostnader").Total);
        Assert.Equal(6000m, netResult);
        Assert.True(netResult > 0, "Net result should be positive (profit)");
    }

    [Fact]
    public async Task ZeroAmountAccounts_AreExcluded()
    {
        // Only create a transaction for one revenue account; the other should be excluded
        await CreateEntry(new DateOnly(2026, 1, 10), "Sale", _cashAccount.Id, _revenueAccount1.Id, 1000m);

        var (sections, _) = await _service.GetIncomeStatementAsync(_fiscalYear.Id);

        var revenue = sections.Single(s => s.Title == "Intäkter");
        Assert.Single(revenue.Rows);
        Assert.Equal("3010", revenue.Rows[0].AccountNumber);

        // Expense section should have no rows since no expense transactions
        var expenses = sections.Single(s => s.Title == "Kostnader");
        Assert.Empty(expenses.Rows);
    }

    [Fact]
    public async Task EmptyFiscalYear_ReturnsEmptySections()
    {
        var emptyFy = new FiscalYear
        {
            Name = "2027",
            StartDate = new DateOnly(2027, 1, 1),
            EndDate = new DateOnly(2027, 12, 31)
        };
        _db.FiscalYears.Add(emptyFy);
        await _db.SaveChangesAsync();

        var (sections, netResult) = await _service.GetIncomeStatementAsync(emptyFy.Id);

        Assert.Equal(2, sections.Count);
        Assert.Empty(sections[0].Rows);
        Assert.Empty(sections[1].Rows);
        Assert.Equal(0m, sections[0].Total);
        Assert.Equal(0m, sections[1].Total);
        Assert.Equal(0m, netResult);
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
