using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

/// <summary>
/// P0 #3: Draft entry filtering tests.
/// Current bug: Reports (trial balance, balance sheet, income statement)
/// include ALL entries regardless of IsPosted status.
/// Only Posted entries should be included in reports.
/// </summary>
public class DraftFilteringTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly JournalEntryService _service;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _assetAccount;
    private readonly Account _revenueAccount;
    private readonly Account _expenseAccount;

    public DraftFilteringTests()
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

        _assetAccount = new Account
        {
            AccountNumber = "1910",
            Name = "Kassa",
            AccountClass = AccountClass.Asset,
            FiscalYearId = _fiscalYear.Id
        };
        _revenueAccount = new Account
        {
            AccountNumber = "3010",
            Name = "Försäljning",
            AccountClass = AccountClass.Revenue,
            FiscalYearId = _fiscalYear.Id
        };
        _expenseAccount = new Account
        {
            AccountNumber = "5010",
            Name = "Lokalhyra",
            AccountClass = AccountClass.Expense,
            FiscalYearId = _fiscalYear.Id
        };
        _db.Accounts.AddRange(_assetAccount, _revenueAccount, _expenseAccount);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task TrialBalance_OnlyIncludesPostedEntries()
    {
        // Create and post one entry (1000)
        var (posted, _) = await _service.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 1000m));
        await _service.PostAsync(posted!.Id);

        // Create a draft entry (2000) — NOT posted
        await _service.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 2000m));

        var rows = await _service.GetTrialBalanceAsync(_fiscalYear.Id);
        var asset = rows.Single(r => r.AccountNumber == "1910");

        // Only the posted entry (1000) should be counted
        Assert.Equal(1000m, asset.TotalDebit);
        Assert.Equal(0m, asset.TotalCredit);
    }

    [Fact]
    public async Task TrialBalance_DraftEntry_DoesNotAppear()
    {
        // Create ONLY a draft entry
        await _service.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 500m));

        var rows = await _service.GetTrialBalanceAsync(_fiscalYear.Id);

        // No posted entries → trial balance should be empty
        Assert.Empty(rows);
    }

    [Fact]
    public async Task BalanceSheet_ExcludesDraftEntries()
    {
        // Give asset an IB so it shows up even without posted transactions
        _assetAccount.IncomingBalance = 10000m;
        await _db.SaveChangesAsync();

        // Create and post one entry
        var (posted, _) = await _service.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 1000m));
        await _service.PostAsync(posted!.Id);

        // Create a draft entry — should be excluded
        await _service.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 5000m));

        var sections = await _service.GetBalanceSheetAsync(_fiscalYear.Id);
        var assets = sections.Single(s => s.Title == "Tillgångar");
        var cash = assets.Rows.Single(r => r.AccountNumber == "1910");

        // Only the posted debit of 1000 should be counted
        Assert.Equal(1000m, cash.PeriodDebit);
        Assert.Equal(0m, cash.PeriodCredit);
    }

    [Fact]
    public async Task IncomeStatement_ExcludesDraftEntries()
    {
        // Create and post a revenue entry
        var (posted, _) = await _service.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 3000m));
        await _service.PostAsync(posted!.Id);

        // Create a draft expense entry — should be excluded
        await _service.CreateAsync(MakeEntry(_expenseAccount.Id, _assetAccount.Id, 1000m));

        var (sections, netResult) = await _service.GetIncomeStatementAsync(_fiscalYear.Id);

        var revenue = sections.Single(s => s.Title == "Intäkter");
        Assert.Equal(3000m, revenue.Total);

        var expenses = sections.Single(s => s.Title == "Kostnader");
        // Draft expense should NOT be counted
        Assert.Equal(0m, expenses.Total);

        Assert.Equal(3000m, netResult);
    }

    [Fact]
    public async Task TrialBalance_MultipleEntries_OnlyCountsPosted()
    {
        // Posted entry 1
        var (p1, _) = await _service.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 1000m));
        await _service.PostAsync(p1!.Id);

        // Draft entry
        await _service.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 2000m));

        // Posted entry 2
        var (p2, _) = await _service.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 3000m));
        await _service.PostAsync(p2!.Id);

        var rows = await _service.GetTrialBalanceAsync(_fiscalYear.Id);
        var asset = rows.Single(r => r.AccountNumber == "1910");

        // Only posted entries: 1000 + 3000 = 4000
        Assert.Equal(4000m, asset.TotalDebit);
    }

    private JournalEntry MakeEntry(int debitAccountId, int creditAccountId, decimal amount) => new()
    {
        Date = new DateOnly(2026, 3, 1),
        Description = $"Test entry {amount}",
        FiscalYearId = _fiscalYear.Id,
        Lines =
        [
            new() { AccountId = debitAccountId, DebitAmount = amount, CreditAmount = 0 },
            new() { AccountId = creditAccountId, DebitAmount = 0, CreditAmount = amount }
        ]
    };
}
