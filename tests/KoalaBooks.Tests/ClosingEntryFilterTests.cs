using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

/// <summary>
/// Regression tests for closing entry filtering in reports.
/// Bug: After year-end closing, income statement should exclude closing entries
/// (IsClosingEntry=true) so the P&amp;L isn't zeroed out. Balance sheet must
/// include them to show final state. Trial balance should support filtering.
/// </summary>
public class ClosingEntryFilterTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;
    private readonly Account _expense;

    public ClosingEntryFilterTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        var accounts = _f.CreateStandardAccounts(_fy.Id);
        _cash = accounts.Cash;
        _revenue = accounts.Revenue;
        _expense = accounts.Expense;
    }

    public void Dispose() => _f.Dispose();

    /// <summary>
    /// After year-end closing creates closing entries, the income statement must
    /// still show original revenue/expenses — not the zeroed-out view. Closing
    /// entries should be excluded from P&amp;L computation.
    /// </summary>
    [Fact]
    public async Task IncomeStatement_AfterClosing_ExcludesClosingEntries()
    {
        // Arrange: normal year with revenue 10,000 and expense 6,000
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 10_000m);
        await _f.CreateAndPostEntryAsync(_fy.Id, _expense.Id, _cash.Id, 6_000m);

        // Add result accounts and execute closing
        _f.CreateAccount(_fy.Id, "8999", "Årets resultat", AccountClass.Expense);
        _f.CreateAccount(_fy.Id, "2099", "Årets resultat", AccountClass.Equity);
        await _f.YearEndClosingService.ExecuteClosingAsync(_fy.Id);

        // Act
        var (sections, netResult) = await _f.JournalEntryService.GetIncomeStatementAsync(_fy.Id);

        // Assert: income statement should still show the original P&L, not zero
        var revSection = sections.Single(s => s.Title == "Intäkter");
        Assert.Equal(10_000m, revSection.Total);

        var expSection = sections.Single(s => s.Title == "Kostnader");
        Assert.Equal(6_000m, expSection.Total);

        Assert.Equal(4_000m, netResult);
    }

    /// <summary>
    /// Balance sheet must include closing entries — it should reflect the final
    /// state after year-end closing (equity adjusted, P&amp;L accounts zeroed).
    /// </summary>
    [Fact]
    public async Task BalanceSheet_AfterClosing_IncludesClosingEntries()
    {
        // Arrange
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 10_000m);
        await _f.CreateAndPostEntryAsync(_fy.Id, _expense.Id, _cash.Id, 6_000m);

        _f.CreateAccount(_fy.Id, "8999", "Årets resultat", AccountClass.Expense);
        _f.CreateAccount(_fy.Id, "2099", "Årets resultat", AccountClass.Equity);
        await _f.YearEndClosingService.ExecuteClosingAsync(_fy.Id);

        // Act
        var sections = await _f.JournalEntryService.GetBalanceSheetAsync(_fy.Id);

        // Assert: equity section should include 2099 with the net result
        var equitySection = sections.Single(s => s.Title == "Eget kapital");
        var a2099 = equitySection.Rows.SingleOrDefault(r => r.AccountNumber == "2099");
        Assert.NotNull(a2099);
        Assert.Equal(4_000m, a2099.ClosingBalance);
    }

    /// <summary>
    /// Trial balance excludes closing entries by default (excludeClosingEntries=true),
    /// showing the pre-closing P&amp;L view. When includeClosingEntries is requested,
    /// closing entries are included — P&amp;L accounts should then show zero.
    /// </summary>
    [Fact]
    public async Task TrialBalance_ExcludesClosingEntries_WhenRequested()
    {
        // Arrange
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 10_000m);
        await _f.CreateAndPostEntryAsync(_fy.Id, _expense.Id, _cash.Id, 6_000m);

        _f.CreateAccount(_fy.Id, "8999", "Årets resultat", AccountClass.Expense);
        _f.CreateAccount(_fy.Id, "2099", "Årets resultat", AccountClass.Equity);
        await _f.YearEndClosingService.ExecuteClosingAsync(_fy.Id);

        // Act: default trial balance (excludeClosingEntries=true)
        var rows = await _f.JournalEntryService.GetTrialBalanceAsync(_fy.Id);

        // Assert: closing entries excluded — P&L should show original values
        var revenueRow = rows.Single(r => r.AccountNumber == "3010");
        Assert.Equal(10_000m, revenueRow.Balance);

        var expenseRow = rows.Single(r => r.AccountNumber == "5010");
        Assert.Equal(6_000m, expenseRow.Balance);

        // 8999/2099 should NOT appear (they only have closing entry transactions)
        Assert.DoesNotContain(rows, r => r.AccountNumber == "8999");
        Assert.DoesNotContain(rows, r => r.AccountNumber == "2099");

        // Act: trial balance with closing entries included
        var allRows = await _f.JournalEntryService.GetTrialBalanceAsync(_fy.Id, excludeClosingEntries: false);

        // 8999 and 2099 should now appear
        Assert.Contains(allRows, r => r.AccountNumber == "8999" || r.AccountNumber == "2099");
    }
}
