using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

/// <summary>
/// P0 #2: Balance formula tests.
/// Current bug: All accounts use asset-normal math (Debit - Credit).
/// Credit-normal accounts (Liability, Equity, Revenue) should use Credit - Debit.
/// </summary>
public class BalanceFormulaTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _assetAccount;
    private readonly Account _liabilityAccount;
    private readonly Account _equityAccount;
    private readonly Account _revenueAccount;
    private readonly Account _expenseAccount;

    public BalanceFormulaTests()
    {
        _f = new TestFixture();
        _fiscalYear = _f.CreateFiscalYear();
        _assetAccount = _f.CreateAccount(_fiscalYear.Id, "1910", "Kassa");
        _liabilityAccount = _f.CreateAccount(_fiscalYear.Id, "2440", "Leverantörsskulder", AccountClass.Liability);
        _equityAccount = _f.CreateAccount(_fiscalYear.Id, "2081", "Aktiekapital", AccountClass.Equity);
        _revenueAccount = _f.CreateAccount(_fiscalYear.Id, "3010", "Försäljning", AccountClass.Revenue);
        _expenseAccount = _f.CreateAccount(_fiscalYear.Id, "4010", "Inköp", AccountClass.Expense);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task LiabilityAccount_Credited1000_TrialBalanceShowsPositive1000()
    {
        // Debit Asset (Kassa), Credit Liability (Leverantörsskulder) — paying supplier creates liability
        await CreateAndPostEntry(_assetAccount.Id, _liabilityAccount.Id, 1000m);

        var rows = await _f.JournalEntryService.GetTrialBalanceAsync(_fiscalYear.Id);
        var liability = rows.Single(r => r.AccountNumber == "2440");

        // Liability is credit-normal: credited 1000 should show balance +1000
        Assert.Equal(1000m, liability.Balance);
    }

    [Fact]
    public async Task AssetAccount_Debited1000_TrialBalanceShowsPositive1000()
    {
        // Debit Asset (Kassa), Credit Revenue (Försäljning)
        await CreateAndPostEntry(_assetAccount.Id, _revenueAccount.Id, 1000m);

        var rows = await _f.JournalEntryService.GetTrialBalanceAsync(_fiscalYear.Id);
        var asset = rows.Single(r => r.AccountNumber == "1910");

        // Asset is debit-normal: debited 1000 should show balance +1000
        Assert.Equal(1000m, asset.Balance);
    }

    [Fact]
    public async Task RevenueAccount_Credited5000_TrialBalanceShowsPositive5000()
    {
        // Debit Asset (Kassa), Credit Revenue (Försäljning)
        await CreateAndPostEntry(_assetAccount.Id, _revenueAccount.Id, 5000m);

        var rows = await _f.JournalEntryService.GetTrialBalanceAsync(_fiscalYear.Id);
        var revenue = rows.Single(r => r.AccountNumber == "3010");

        // Revenue is credit-normal: credited 5000 should show balance +5000
        Assert.Equal(5000m, revenue.Balance);
    }

    [Fact]
    public async Task ExpenseAccount_Debited3000_TrialBalanceShowsPositive3000()
    {
        // Debit Expense (Inköp), Credit Asset (Kassa)
        await CreateAndPostEntry(_expenseAccount.Id, _assetAccount.Id, 3000m);

        var rows = await _f.JournalEntryService.GetTrialBalanceAsync(_fiscalYear.Id);
        var expense = rows.Single(r => r.AccountNumber == "4010");

        // Expense is debit-normal: debited 3000 should show balance +3000
        Assert.Equal(3000m, expense.Balance);
    }

    [Fact]
    public async Task LiabilityAccount_MixedTransactions_ShowsCorrectBalance()
    {
        // Credit Liability 2000 (Debit Asset, Credit Liability)
        await CreateAndPostEntry(_assetAccount.Id, _liabilityAccount.Id, 2000m);
        // Debit Liability 500 (partial repayment: Debit Liability, Credit Asset)
        await CreateAndPostEntry(_liabilityAccount.Id, _assetAccount.Id, 500m);

        var rows = await _f.JournalEntryService.GetTrialBalanceAsync(_fiscalYear.Id);
        var liability = rows.Single(r => r.AccountNumber == "2440");

        // Net: credit 2000, debit 500 → balance should be 1500
        Assert.Equal(1500m, liability.Balance);
    }

    [Fact]
    public async Task EquityAccount_Credited_TrialBalanceShowsPositive()
    {
        // Debit Asset, Credit Equity (owner investment)
        await CreateAndPostEntry(_assetAccount.Id, _equityAccount.Id, 10000m);

        var rows = await _f.JournalEntryService.GetTrialBalanceAsync(_fiscalYear.Id);
        var equity = rows.Single(r => r.AccountNumber == "2081");

        // Equity is credit-normal: credited 10000 should show balance +10000
        Assert.Equal(10000m, equity.Balance);
    }

    [Fact]
    public async Task BalanceSheet_LiabilityClosingBalance_IsPositiveForCredits()
    {
        // Debit Asset 1000, Credit Liability 1000
        await CreateAndPostEntry(_assetAccount.Id, _liabilityAccount.Id, 1000m);

        var sections = await _f.JournalEntryService.GetBalanceSheetAsync(_fiscalYear.Id);
        var liabilities = sections.Single(s => s.Title == "Skulder");
        var row = liabilities.Rows.Single(r => r.AccountNumber == "2440");

        // Liability closing balance should be positive when credited
        Assert.Equal(1000m, row.ClosingBalance);
    }

    [Fact]
    public async Task BalanceSheet_EquityClosingBalance_IsPositiveForCredits()
    {
        // Debit Asset 5000, Credit Equity 5000
        await CreateAndPostEntry(_assetAccount.Id, _equityAccount.Id, 5000m);

        var sections = await _f.JournalEntryService.GetBalanceSheetAsync(_fiscalYear.Id);
        var equity = sections.Single(s => s.Title == "Eget kapital");
        var row = equity.Rows.Single(r => r.AccountNumber == "2081");

        Assert.Equal(5000m, row.ClosingBalance);
    }

    [Fact]
    public async Task BalanceSheet_WithIncomingBalance_LiabilityCalculatesCorrectly()
    {
        // Give liability an incoming balance
        _liabilityAccount.IncomingBalance = 3000m;
        await _f.Db.SaveChangesAsync();

        // Credit Liability another 1000
        await CreateAndPostEntry(_assetAccount.Id, _liabilityAccount.Id, 1000m);

        var sections = await _f.JournalEntryService.GetBalanceSheetAsync(_fiscalYear.Id);
        var liabilities = sections.Single(s => s.Title == "Skulder");
        var row = liabilities.Rows.Single(r => r.AccountNumber == "2440");

        // IB 3000 + credit 1000 = 4000
        Assert.Equal(4000m, row.ClosingBalance);
    }

    private async Task CreateAndPostEntry(int debitAccountId, int creditAccountId, decimal amount)
    {
        var entry = new JournalEntry
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
        var (created, error) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Null(error);
        Assert.NotNull(created);
        var postError = await _f.JournalEntryService.PostAsync(created.Id);
        Assert.Null(postError);
    }
}
