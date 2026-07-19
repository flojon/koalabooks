using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Tests;

public class VatQuarterHelperTests
{
    // ── Standard fiscal year (Jan–Dec 2026) ──────────────────────────

    [Theory]
    [InlineData(1, 2026,  1,  1, 2026,  3, 31)]
    [InlineData(2, 2026,  4,  1, 2026,  6, 30)]
    [InlineData(3, 2026,  7,  1, 2026,  9, 30)]
    [InlineData(4, 2026, 10,  1, 2026, 12, 31)]
    public void ComputeRange_StandardFiscalYear_ReturnsCalendarQuarter(
        int quarter, int ey, int em, int ed, int ey2, int em2, int ed2)
    {
        var fy = Fy(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var range = VatQuarterHelper.ComputeRange(fy, quarter);

        Assert.NotNull(range);
        Assert.Equal(new DateOnly(ey, em, ed), range.Value.From);
        Assert.Equal(new DateOnly(ey2, em2, ed2), range.Value.To);
    }

    // ── Broken fiscal year (Jul 2025–Jun 2026) ───────────────────────
    // K3/K4 belong to the start year; K1/K2 belong to the end year.

    [Theory]
    [InlineData(3, 2025,  7,  1, 2025,  9, 30)]
    [InlineData(4, 2025, 10,  1, 2025, 12, 31)]
    [InlineData(1, 2026,  1,  1, 2026,  3, 31)]
    [InlineData(2, 2026,  4,  1, 2026,  6, 30)]
    public void ComputeRange_BrokenFiscalYear_PicksCorrectYear(
        int quarter, int ey, int em, int ed, int ey2, int em2, int ed2)
    {
        var fy = Fy(new DateOnly(2025, 7, 1), new DateOnly(2026, 6, 30));

        var range = VatQuarterHelper.ComputeRange(fy, quarter);

        Assert.NotNull(range);
        Assert.Equal(new DateOnly(ey, em, ed), range.Value.From);
        Assert.Equal(new DateOnly(ey2, em2, ed2), range.Value.To);
    }

    // ── Partial fiscal year (Nov–Dec 2025): K4 clamped, others empty ─

    [Fact]
    public void ComputeRange_PartialFY_K4_ClampedToFYStart()
    {
        var fy = Fy(new DateOnly(2025, 11, 1), new DateOnly(2025, 12, 31));

        var range = VatQuarterHelper.ComputeRange(fy, 4);

        Assert.NotNull(range);
        Assert.Equal(new DateOnly(2025, 11, 1), range.Value.From); // clamped from Oct 1
        Assert.Equal(new DateOnly(2025, 12, 31), range.Value.To);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ComputeRange_PartialFY_OutOfRangeQuarters_ReturnsNull(int quarter)
    {
        var fy = Fy(new DateOnly(2025, 11, 1), new DateOnly(2025, 12, 31));

        Assert.Null(VatQuarterHelper.ComputeRange(fy, quarter));
    }

    // ── Broken FY where quarter straddles the FY start ───────────────
    // FY starts Oct 1 2025 → K4 should be clamped to Oct 1 (not start of Oct which matches).

    [Fact]
    public void ComputeRange_BrokenFY_QuarterStraddlesFYStart_ClampedCorrectly()
    {
        // FY: Oct 2025 – Sep 2026 (exactly 12 months, broken)
        var fy = Fy(new DateOnly(2025, 10, 1), new DateOnly(2026, 9, 30));

        // K4 in 2025: Oct 1 – Dec 31 → fully inside the FY
        var k4 = VatQuarterHelper.ComputeRange(fy, 4);
        Assert.NotNull(k4);
        Assert.Equal(new DateOnly(2025, 10, 1), k4.Value.From);
        Assert.Equal(new DateOnly(2025, 12, 31), k4.Value.To);

        // K3 in 2026: Jul 1 – Sep 30 → fully inside the FY
        var k3 = VatQuarterHelper.ComputeRange(fy, 3);
        Assert.NotNull(k3);
        Assert.Equal(new DateOnly(2026, 7, 1), k3.Value.From);
        Assert.Equal(new DateOnly(2026, 9, 30), k3.Value.To);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void ComputeRange_InvalidQuarter_Throws(int quarter)
    {
        var fy = Fy(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        Assert.Throws<ArgumentOutOfRangeException>(() => VatQuarterHelper.ComputeRange(fy, quarter));
    }

    private static FiscalYear Fy(DateOnly start, DateOnly end) =>
        new() { Name = "test", StartDate = start, EndDate = end, OrganisationId = 1 };
}
