using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class BalanceSheetTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly JournalEntryService _service;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _cashAccount;
    private readonly Account _bankAccount;
    private readonly Account _liabilityAccount;
    private readonly Account _equityAccount;
    private readonly Account _zeroAccount;

    public BalanceSheetTests()
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
        _bankAccount = new Account
        {
            AccountNumber = "1920",
            Name = "Bank",
            AccountClass = AccountClass.Asset,
            IncomingBalance = 5000m,
            FiscalYearId = _fiscalYear.Id
        };
        _liabilityAccount = new Account
        {
            AccountNumber = "2440",
            Name = "Leverantörsskulder",
            AccountClass = AccountClass.Liability,
            IncomingBalance = 3000m,
            FiscalYearId = _fiscalYear.Id
        };
        _equityAccount = new Account
        {
            AccountNumber = "2081",
            Name = "Aktiekapital",
            AccountClass = AccountClass.Equity,
            IncomingBalance = 12000m,
            FiscalYearId = _fiscalYear.Id
        };
        _zeroAccount = new Account
        {
            AccountNumber = "1510",
            Name = "Kundfordringar",
            AccountClass = AccountClass.Asset,
            IncomingBalance = 0m,
            FiscalYearId = _fiscalYear.Id
        };
        _db.Accounts.AddRange(_cashAccount, _bankAccount, _liabilityAccount, _equityAccount, _zeroAccount);

        // Also add revenue/expense accounts for balanced entries
        var revenueAccount = new Account
        {
            AccountNumber = "3010",
            Name = "Försäljning",
            AccountClass = AccountClass.Revenue,
            IncomingBalance = 0m,
            FiscalYearId = _fiscalYear.Id
        };
        _db.Accounts.Add(revenueAccount);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task BalanceSheet_GroupsIntoThreeSections()
    {
        var sections = await _service.GetBalanceSheetAsync(_fiscalYear.Id);

        Assert.Equal(3, sections.Count);
        Assert.Equal("Tillgångar", sections[0].Title);
        Assert.Equal("Skulder", sections[1].Title);
        Assert.Equal("Eget kapital", sections[2].Title);
    }

    [Fact]
    public async Task BalanceSheet_ClosingBalance_EqualsIBPlusDebitMinusCredit()
    {
        // Cash debit 2000 (asset receives cash), revenue credit 2000
        await CreateEntry(new DateOnly(2026, 2, 1), "Sale",
            _cashAccount.Id, GetRevenueAccountId(), 2000m);

        var sections = await _service.GetBalanceSheetAsync(_fiscalYear.Id);

        var assets = sections.Single(s => s.Title == "Tillgångar");
        var cash = assets.Rows.Single(r => r.AccountNumber == "1910");

        Assert.Equal(10000m, cash.IncomingBalance);
        Assert.Equal(2000m, cash.PeriodDebit);
        Assert.Equal(0m, cash.PeriodCredit);
        // Closing = 10000 + 2000 - 0 = 12000
        Assert.Equal(12000m, cash.ClosingBalance);
    }

    [Fact]
    public async Task BalanceSheet_ExcludesAccountsWithZeroIBAndZeroTransactions()
    {
        // _zeroAccount (1510) has IB=0 and no transactions
        var sections = await _service.GetBalanceSheetAsync(_fiscalYear.Id);

        var assets = sections.Single(s => s.Title == "Tillgångar");
        Assert.DoesNotContain(assets.Rows, r => r.AccountNumber == "1510");
    }

    [Fact]
    public async Task BalanceSheet_IncludesAccountsWithNonZeroIBButNoTransactions()
    {
        // _bankAccount has IB=5000, no transactions
        var sections = await _service.GetBalanceSheetAsync(_fiscalYear.Id);

        var assets = sections.Single(s => s.Title == "Tillgångar");
        var bank = assets.Rows.Single(r => r.AccountNumber == "1920");
        Assert.Equal(5000m, bank.IncomingBalance);
        Assert.Equal(5000m, bank.ClosingBalance);
    }

    [Fact]
    public async Task BalanceSheet_IncludesAccountsWithZeroIBButTransactions()
    {
        // Add a transaction to the zero account (customer receivable)
        await CreateEntry(new DateOnly(2026, 3, 1), "Customer invoice",
            _zeroAccount.Id, GetRevenueAccountId(), 1500m);

        var sections = await _service.GetBalanceSheetAsync(_fiscalYear.Id);

        var assets = sections.Single(s => s.Title == "Tillgångar");
        var receivable = assets.Rows.Single(r => r.AccountNumber == "1510");
        Assert.Equal(0m, receivable.IncomingBalance);
        Assert.Equal(1500m, receivable.PeriodDebit);
        Assert.Equal(1500m, receivable.ClosingBalance);
    }

    [Fact]
    public async Task BalanceSheet_SectionsSortedByAccountNumber()
    {
        var sections = await _service.GetBalanceSheetAsync(_fiscalYear.Id);

        var assets = sections.Single(s => s.Title == "Tillgångar");
        var accountNumbers = assets.Rows.Select(r => r.AccountNumber).ToList();
        Assert.Equal(accountNumbers.OrderBy(n => n).ToList(), accountNumbers);
    }

    [Fact]
    public async Task BalanceSheet_SectionTotalIsSumOfClosingBalances()
    {
        await CreateEntry(new DateOnly(2026, 1, 15), "Sale",
            _cashAccount.Id, GetRevenueAccountId(), 3000m);

        var sections = await _service.GetBalanceSheetAsync(_fiscalYear.Id);

        var assets = sections.Single(s => s.Title == "Tillgångar");
        var expectedTotal = assets.Rows.Sum(r => r.ClosingBalance);
        Assert.Equal(expectedTotal, assets.Total);
    }

    [Fact]
    public async Task BalanceSheet_ExcludesRevenueAndExpenseAccounts()
    {
        // Revenue account (3010) exists but should not appear
        await CreateEntry(new DateOnly(2026, 1, 15), "Sale",
            _cashAccount.Id, GetRevenueAccountId(), 1000m);

        var sections = await _service.GetBalanceSheetAsync(_fiscalYear.Id);

        var allAccountNumbers = sections.SelectMany(s => s.Rows).Select(r => r.AccountNumber).ToList();
        Assert.DoesNotContain("3010", allAccountNumbers);
    }

    [Fact]
    public async Task BalanceSheet_AssetsEqualLiabilitiesPlusEquity_WhenBalanced()
    {
        // IB: Assets (10000+5000) = 15000, Liabilities (3000) + Equity (12000) = 15000 ✓
        // No transactions that would unbalance the sheet
        var sections = await _service.GetBalanceSheetAsync(_fiscalYear.Id);

        var totalAssets = sections.Single(s => s.Title == "Tillgångar").Total;
        var totalLiabilities = sections.Single(s => s.Title == "Skulder").Total;
        var totalEquity = sections.Single(s => s.Title == "Eget kapital").Total;

        Assert.Equal(totalAssets, totalLiabilities + totalEquity);
    }

    private int GetRevenueAccountId()
    {
        return _db.Accounts.Single(a => a.AccountNumber == "3010" && a.FiscalYearId == _fiscalYear.Id).Id;
    }

    private async Task CreateEntry(DateOnly date, string description, int debitAccountId, int creditAccountId, decimal amount)
    {
        var entry = new JournalEntry
        {
            Date = date,
            Description = description,
            FiscalYearId = _fiscalYear.Id,
            IsPosted = true,
            Lines =
            [
                new() { AccountId = debitAccountId, DebitAmount = amount, CreditAmount = 0 },
                new() { AccountId = creditAccountId, DebitAmount = 0, CreditAmount = amount }
            ]
        };
        await _service.CreateAsync(entry);
    }
}
