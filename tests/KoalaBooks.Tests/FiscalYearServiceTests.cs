using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class FiscalYearServiceTests : IDisposable
{
    private readonly TestFixture _f;

    public FiscalYearServiceTests()
    {
        _f = new TestFixture();
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task CreateAsync_ValidFiscalYear_CreatesSuccessfully()
    {
        var fy = await _f.FiscalYearService.CreateAsync(new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        });

        Assert.NotNull(fy);
        Assert.True(fy.Id > 0);
        Assert.Equal("2026", fy.Name);

        var fromDb = await _f.Db.FiscalYears.FindAsync(fy.Id);
        Assert.NotNull(fromDb);
    }

    [Fact]
    public async Task CreateAsync_OverlappingFiscalYear_Throws()
    {
        await _f.FiscalYearService.CreateAsync(new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _f.FiscalYearService.CreateAsync(new FiscalYear
            {
                Name = "2026-H2",
                StartDate = new DateOnly(2026, 7, 1),
                EndDate = new DateOnly(2027, 6, 30)
            }));

        Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetActiveAsync_ReturnsNonClosedYear()
    {
        _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);
        var open = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var active = await _f.FiscalYearService.GetActiveAsync();

        Assert.NotNull(active);
        Assert.Equal(open.Id, active.Id);
        Assert.False(active.IsClosed);
    }

    [Fact]
    public async Task GetActiveAsync_MultipleOpenYears_PrefersExplicitSelection()
    {
        var y2025 = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        // Both years are open (2025 awaiting bokslut, 2026 already in use).
        // A user working through 2025's gap explanations/late invoices should
        // stay in 2025 once they've explicitly switched to it.
        _f.SetSelectedFiscalYear(y2025.Id);

        var active = await _f.FiscalYearService.GetActiveAsync();

        Assert.NotNull(active);
        Assert.Equal(y2025.Id, active.Id);
    }

    [Fact]
    public async Task GetActiveAsync_SelectionPointsToClosedYear_FallsBackToDefault()
    {
        var y2025 = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var y2026 = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        _f.SetSelectedFiscalYear(y2025.Id);
        y2025.IsClosed = true;
        _f.Db.SaveChanges();

        var active = await _f.FiscalYearService.GetActiveAsync();

        Assert.NotNull(active);
        Assert.Equal(y2026.Id, active.Id);
    }

    [Fact]
    public async Task GetActiveAsync_SelectionIsUnknownId_FallsBackToDefault()
    {
        var open = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        _f.SetSelectedFiscalYear(999_999);

        var active = await _f.FiscalYearService.GetActiveAsync();

        Assert.NotNull(active);
        Assert.Equal(open.Id, active.Id);
    }

    [Fact]
    public async Task GetActiveAsync_NoSelection_PrefersYearContainingToday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // "Old" year is still ongoing today and hasn't been closed yet.
        var oldYear = _f.CreateFiscalYear("Old",
            today.AddYears(-1).AddDays(1), today);

        // "Next" year was created early (starts tomorrow) while Old awaits closing.
        // Latest-StartDate ordering alone would wrongly prefer this one.
        _f.CreateFiscalYear("Next",
            today.AddDays(1), today.AddYears(1));

        var active = await _f.FiscalYearService.GetActiveAsync();

        Assert.NotNull(active);
        Assert.Equal(oldYear.Id, active.Id);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllYears()
    {
        var fy1 = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var fy2 = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var all = await _f.FiscalYearService.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, f => f.Id == fy1.Id);
        Assert.Contains(all, f => f.Id == fy2.Id);
    }

    [Fact]
    public async Task PropagateBalances_FollowsPreviousFiscalYearIdLink()
    {
        var source = _f.CreateFiscalYear("2024",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31), isClosed: true);
        _f.CreateAccount(source.Id, "1910", "Kassa", AccountClass.Asset,
            incomingBalance: 0, outgoingBalance: 500);

        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        target.PreviousFiscalYearId = source.Id;
        _f.Db.SaveChanges();
        _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset,
            incomingBalance: 0, outgoingBalance: 0);

        // unrelated year between source and target — should NOT be chosen
        _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));

        await _f.FiscalYearService.PropagateBalancesToNextYearAsync(source.Id);

        var ib = await _f.Db.Accounts
            .Where(a => a.FiscalYearId == target.Id && a.AccountNumber == "1910")
            .Select(a => a.IncomingBalance)
            .FirstAsync();
        Assert.Equal(500, ib);
    }

    [Fact]
    public async Task CopyAccountsFromPreviousYear_SetsPreviousFiscalYearId()
    {
        var prev = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);
        _f.CreateAccount(prev.Id, "1910", "Kassa", AccountClass.Asset,
            outgoingBalance: 100);

        var newFy = await _f.FiscalYearService.CreateAsync(new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        });

        await _f.Db.Entry(newFy).ReloadAsync();
        Assert.Equal(prev.Id, newFy.PreviousFiscalYearId);
    }
}
