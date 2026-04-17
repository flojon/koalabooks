using KoalaBooks.Domain.Entities;
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
}
