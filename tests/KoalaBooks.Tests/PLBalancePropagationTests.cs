using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

/// <summary>
/// Regression tests for P&amp;L balance propagation during fiscal year copy.
/// Bug: When CopyAccountsFromPreviousYear copies P&amp;L accounts (Revenue/Expense),
/// it should set IB=0 — not carry over the UB, since P&amp;L resets each year.
/// Balance sheet accounts (Asset/Liability/Equity) should keep IB = prev UB.
/// </summary>
public class PLBalancePropagationTests : IDisposable
{
    private readonly TestFixture _f;

    public PLBalancePropagationTests()
    {
        _f = new TestFixture();
    }

    public void Dispose() => _f.Dispose();

    /// <summary>
    /// Revenue and expense accounts must get IB=0 in the new fiscal year,
    /// regardless of the previous year's outgoing balance. P&amp;L accounts
    /// reset every year per accounting standards.
    /// </summary>
    [Fact]
    public async Task CopyAccounts_PLAccountsGetZeroIB()
    {
        // Arrange: previous year with P&L accounts that have non-zero UB
        var fy2025 = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));

        _f.CreateAccount(fy2025.Id, "3010", "Försäljning", AccountClass.Revenue,
            outgoingBalance: 50_000m);
        _f.CreateAccount(fy2025.Id, "5010", "Lokalhyra", AccountClass.Expense,
            outgoingBalance: 20_000m);
        _f.CreateAccount(fy2025.Id, "4010", "Inköp", AccountClass.Expense,
            outgoingBalance: 15_000m);

        // Act: create new fiscal year (triggers CopyAccountsFromPreviousYear)
        var fy2026 = await _f.FiscalYearService.CreateAsync(new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        });

        // Assert: P&L accounts should have IB = 0
        var newAccounts = await _f.Db.Accounts
            .Where(a => a.FiscalYearId == fy2026.Id)
            .ToListAsync();

        var revenue = newAccounts.SingleOrDefault(a => a.AccountNumber == "3010");
        Assert.NotNull(revenue);
        Assert.Equal(0m, revenue.IncomingBalance);

        var expense1 = newAccounts.SingleOrDefault(a => a.AccountNumber == "5010");
        Assert.NotNull(expense1);
        Assert.Equal(0m, expense1.IncomingBalance);

        var expense2 = newAccounts.SingleOrDefault(a => a.AccountNumber == "4010");
        Assert.NotNull(expense2);
        Assert.Equal(0m, expense2.IncomingBalance);
    }

    /// <summary>
    /// Asset, Liability, and Equity accounts must carry over IB = previous UB.
    /// This is the standard balance sheet continuity rule.
    /// </summary>
    [Fact]
    public async Task CopyAccounts_BalanceSheetAccountsKeepUB()
    {
        // Arrange: previous year with balance sheet accounts
        var fy2025 = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));

        _f.CreateAccount(fy2025.Id, "1910", "Kassa", AccountClass.Asset,
            outgoingBalance: 100_000m);
        _f.CreateAccount(fy2025.Id, "2440", "Leverantörsskulder", AccountClass.Liability,
            outgoingBalance: 30_000m);
        _f.CreateAccount(fy2025.Id, "2081", "Aktiekapital", AccountClass.Equity,
            outgoingBalance: 70_000m);

        // Act
        var fy2026 = await _f.FiscalYearService.CreateAsync(new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        });

        // Assert: B/S accounts should have IB = previous UB
        var newAccounts = await _f.Db.Accounts
            .Where(a => a.FiscalYearId == fy2026.Id)
            .ToListAsync();

        var cash = newAccounts.Single(a => a.AccountNumber == "1910");
        Assert.Equal(100_000m, cash.IncomingBalance);

        var liability = newAccounts.Single(a => a.AccountNumber == "2440");
        Assert.Equal(30_000m, liability.IncomingBalance);

        var equity = newAccounts.Single(a => a.AccountNumber == "2081");
        Assert.Equal(70_000m, equity.IncomingBalance);
    }
}
