using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

/// <summary>
/// P0 #3: Draft entry filtering tests.
/// Current bug: Reports (trial balance, balance sheet, income statement)
/// include ALL entries regardless of IsPosted status.
/// Only Posted entries should be included in reports.
/// </summary>
public class DraftFilteringTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _assetAccount;
    private readonly Account _revenueAccount;
    private readonly Account _expenseAccount;

    public DraftFilteringTests()
    {
        _f = new TestFixture();
        _fiscalYear = _f.CreateFiscalYear();
        _assetAccount = _f.CreateAccount(_fiscalYear.Id, "1910", "Kassa");
        _revenueAccount = _f.CreateAccount(_fiscalYear.Id, "3010", "Försäljning", AccountClass.Revenue);
        _expenseAccount = _f.CreateAccount(_fiscalYear.Id, "5010", "Lokalhyra", AccountClass.Expense);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task TrialBalance_OnlyIncludesPostedEntries()
    {
        // Create and post one entry (1000)
        var (posted, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 1000m));
        await _f.JournalEntryService.PostAsync(posted!.Id);

        // Create a draft entry (2000) — NOT posted
        await _f.JournalEntryService.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 2000m));

        var rows = await _f.JournalEntryService.GetTrialBalanceAsync(_fiscalYear.Id);
        var asset = rows.Single(r => r.AccountNumber == "1910");

        // Only the posted entry (1000) should be counted
        Assert.Equal(1000m, asset.TotalDebit);
        Assert.Equal(0m, asset.TotalCredit);
    }

    [Fact]
    public async Task TrialBalance_DraftEntry_DoesNotAppear()
    {
        // Create ONLY a draft entry
        await _f.JournalEntryService.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 500m));

        var rows = await _f.JournalEntryService.GetTrialBalanceAsync(_fiscalYear.Id);

        // No posted entries → trial balance should be empty
        Assert.Empty(rows);
    }

    [Fact]
    public async Task BalanceSheet_ExcludesDraftEntries()
    {
        // Give asset an IB so it shows up even without posted transactions
        _assetAccount.IncomingBalance = 10000m;
        await _f.Db.SaveChangesAsync();

        // Create and post one entry
        var (posted, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 1000m));
        await _f.JournalEntryService.PostAsync(posted!.Id);

        // Create a draft entry — should be excluded
        await _f.JournalEntryService.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 5000m));

        var sections = await _f.JournalEntryService.GetBalanceSheetAsync(_fiscalYear.Id);
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
        var (posted, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 3000m));
        await _f.JournalEntryService.PostAsync(posted!.Id);

        // Create a draft expense entry — should be excluded
        await _f.JournalEntryService.CreateAsync(MakeEntry(_expenseAccount.Id, _assetAccount.Id, 1000m));

        var (sections, netResult) = await _f.JournalEntryService.GetIncomeStatementAsync(_fiscalYear.Id);

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
        var (p1, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 1000m));
        await _f.JournalEntryService.PostAsync(p1!.Id);

        // Draft entry
        await _f.JournalEntryService.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 2000m));

        // Posted entry 2
        var (p2, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(_assetAccount.Id, _revenueAccount.Id, 3000m));
        await _f.JournalEntryService.PostAsync(p2!.Id);

        var rows = await _f.JournalEntryService.GetTrialBalanceAsync(_fiscalYear.Id);
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
