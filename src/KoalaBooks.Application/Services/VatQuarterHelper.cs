using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Application.Services;

public static class VatQuarterHelper
{
    /// <summary>
    /// Returns the [From, To] date range for a Skatteverket calendar quarter (K1–K4)
    /// within the given fiscal year, clamped to fiscal year bounds.
    ///
    /// For broken fiscal years spanning two calendar years (e.g. Jul 2025–Jun 2026),
    /// the quarter is looked up in whichever year it actually falls inside the FY.
    /// If a quarter is entirely outside the fiscal year, From == To (empty range).
    /// </summary>
    public static (DateOnly From, DateOnly To) ComputeRange(FiscalYear fy, int quarter)
    {
        var (qFrom, qTo) = CalendarQuarter(fy.StartDate.Year, quarter);

        // For broken fiscal years, the quarter may belong to the end year instead.
        if (fy.StartDate.Year != fy.EndDate.Year && !Overlaps(qFrom, qTo, fy.StartDate, fy.EndDate))
            (qFrom, qTo) = CalendarQuarter(fy.EndDate.Year, quarter);

        var from = qFrom < fy.StartDate ? fy.StartDate : qFrom;
        var to   = qTo   > fy.EndDate   ? fy.EndDate   : qTo;
        if (to < from) to = from; // quarter is entirely outside this FY
        return (from, to);
    }

    private static (DateOnly From, DateOnly To) CalendarQuarter(int year, int quarter)
    {
        var from = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
        return (from, from.AddMonths(3).AddDays(-1));
    }

    private static bool Overlaps(DateOnly qFrom, DateOnly qTo, DateOnly fyStart, DateOnly fyEnd)
        => qFrom <= fyEnd && qTo >= fyStart;
}
