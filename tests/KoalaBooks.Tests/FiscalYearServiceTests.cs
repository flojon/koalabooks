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
    public async Task GetForDateAsync_DateInsideRange_ReturnsThatYear()
    {
        _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var fy2026 = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = await _f.FiscalYearService.GetForDateAsync(new DateOnly(2026, 6, 15));

        Assert.NotNull(result);
        Assert.Equal(fy2026.Id, result.Id);
    }

    [Fact]
    public async Task GetForDateAsync_TwoOpenYears_PicksTheOneContainingTheDate()
    {
        // Regression test for #283: two simultaneously open fiscal years must not
        // collapse to "whichever started later" — the date decides, not IsClosed.
        var fy2025 = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = await _f.FiscalYearService.GetForDateAsync(new DateOnly(2025, 3, 1));

        Assert.NotNull(result);
        Assert.Equal(fy2025.Id, result.Id);
    }

    [Fact]
    public async Task GetForDateAsync_NoYearCoversDate_ReturnsNull()
    {
        _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = await _f.FiscalYearService.GetForDateAsync(new DateOnly(2030, 1, 1));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetForDateAsync_ClosedYearCoveringDate_IsStillReturned()
    {
        var closed = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);

        var result = await _f.FiscalYearService.GetForDateAsync(new DateOnly(2025, 6, 1));

        Assert.NotNull(result);
        Assert.Equal(closed.Id, result.Id);
    }

    [Fact]
    public async Task GetDefaultFiscalYearAsync_TodayCoveredByAYear_ReturnsIt()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var fy = _f.CreateFiscalYear("Current",
            today.AddMonths(-1), today.AddMonths(1));

        var result = await _f.FiscalYearService.GetDefaultFiscalYearAsync();

        Assert.NotNull(result);
        Assert.Equal(fy.Id, result.Id);
    }

    [Fact]
    public async Task GetDefaultFiscalYearAsync_NoYearCoversToday_FallsBackToLatestOpenYear()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        // Gap: no fiscal year covers "today", but there are two open years, one older.
        _f.CreateFiscalYear("Older open",
            today.AddYears(-2), today.AddYears(-1).AddDays(-1));
        var newerOpen = _f.CreateFiscalYear("Newer open",
            today.AddYears(1), today.AddYears(2));

        var result = await _f.FiscalYearService.GetDefaultFiscalYearAsync();

        Assert.NotNull(result);
        Assert.Equal(newerOpen.Id, result.Id);
    }

    [Fact]
    public async Task GetDefaultFiscalYearAsync_NoOpenYearsAtAll_ReturnsNull()
    {
        _f.CreateFiscalYear("Closed",
            new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), isClosed: true);

        var result = await _f.FiscalYearService.GetDefaultFiscalYearAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOpenFiscalYearsAsync_ExcludesClosedYears_OrderedByStartDateDescending()
    {
        _f.CreateFiscalYear("2024", new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31), isClosed: true);
        var fy2025 = _f.CreateFiscalYear("2025", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var fy2026 = _f.CreateFiscalYear("2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = await _f.FiscalYearService.GetOpenFiscalYearsAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal(fy2026.Id, result[0].Id);
        Assert.Equal(fy2025.Id, result[1].Id);
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
