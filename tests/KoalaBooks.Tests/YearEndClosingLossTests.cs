using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

/// <summary>
/// Tests year-end closing when the company has a net loss (expenses > revenue).
/// The closing should correctly transfer the loss to the equity account (2099).
/// </summary>
public class YearEndClosingLossTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;
    private readonly Account _expense;

    public YearEndClosingLossTests()
    {
        _f = new TestFixture();
        // EndDate must be in the past — closing before a year has ended is now rejected (issue #307).
        _fy = _f.CreateFiscalYear(end: DateOnly.FromDateTime(DateTime.Today).AddDays(-1));
        var accounts = _f.CreateStandardAccounts(_fy.Id);
        _cash = accounts.Cash;
        _revenue = accounts.Revenue;
        _expense = accounts.Expense;
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task ExecuteClosing_WithLoss_TransfersLossToEquity()
    {
        // Revenue 3,000, Expense 8,000 → Net loss of 5,000
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 3_000m);
        await _f.CreateAndPostEntryAsync(_fy.Id, _expense.Id, _cash.Id, 8_000m);

        var result = await _f.YearEndClosingService.ExecuteClosingAsync(_fy.Id);

        Assert.True(result.Success);

        var closingEntries = await _f.Db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Where(j => j.FiscalYearId == _fy.Id && j.IsClosingEntry)
            .OrderBy(j => j.EntryNumber)
            .ToListAsync();

        Assert.Equal(2, closingEntries.Count);

        // Entry 2 transfers loss to equity (8999 → 2099)
        var entry2 = closingEntries[1];
        Assert.Contains("eget kapital", entry2.Description, StringComparison.OrdinalIgnoreCase);

        var line8999 = entry2.Lines.Single(l => l.Account.AccountNumber == "8999");
        var line2099 = entry2.Lines.Single(l => l.Account.AccountNumber == "2099");

        // Loss: 8999 gets credited to zero it out
        Assert.Equal(5000m, line8999.CreditAmount);
        Assert.Equal(0m, line8999.DebitAmount);

        // Loss: 2099 gets debited (reducing equity)
        Assert.Equal(5000m, line2099.DebitAmount);
        Assert.Equal(0m, line2099.CreditAmount);
    }

    [Fact]
    public async Task ExecuteClosing_WithLoss_EquityOutgoingBalanceReflectsLoss()
    {
        // Revenue 3,000, Expense 8,000 → Net loss of 5,000
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 3_000m);
        await _f.CreateAndPostEntryAsync(_fy.Id, _expense.Id, _cash.Id, 8_000m);

        await _f.YearEndClosingService.ExecuteClosingAsync(_fy.Id);

        var a2099 = await _f.Db.Accounts.SingleAsync(
            a => a.FiscalYearId == _fy.Id && a.AccountNumber == "2099");
        Assert.Equal(-5000m, a2099.OutgoingBalance);

        // P&L accounts should be zeroed out
        var revenue = await _f.Db.Accounts.SingleAsync(
            a => a.FiscalYearId == _fy.Id && a.AccountNumber == "3010");
        Assert.Equal(0m, revenue.OutgoingBalance);

        var expense = await _f.Db.Accounts.SingleAsync(
            a => a.FiscalYearId == _fy.Id && a.AccountNumber == "5010");
        Assert.Equal(0m, expense.OutgoingBalance);
    }
}
