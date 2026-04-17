using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

/// <summary>
/// Regression tests for reversal date clamping within fiscal year boundaries.
/// Bug: CreateReversalAsync uses DateTime.Today for the reversal date. If the
/// user creates a reversal after the fiscal year has ended (but before it's
/// closed), the reversal date can escape the FY boundary. It should be clamped
/// to the fiscal year's EndDate.
/// </summary>
public class ReversalDateClampingTests : IDisposable
{
    private readonly TestFixture _f;

    public ReversalDateClampingTests()
    {
        _f = new TestFixture();
    }

    public void Dispose() => _f.Dispose();

    /// <summary>
    /// When today is after the fiscal year end, the reversal date should be
    /// clamped to FiscalYear.EndDate instead of using today's date.
    /// 
    /// NOTE: This test uses a past fiscal year (2024) whose end date is always
    /// before today to deterministically trigger the clamping logic.
    /// </summary>
    [Fact]
    public async Task Reversal_DateClampedToFiscalYearEnd_WhenTodayIsAfter()
    {
        // Arrange: fiscal year 2024, fully in the past
        var fy = _f.CreateFiscalYear("2024",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31));
        var cash = _f.CreateAccount(fy.Id, "1910", "Kassa", AccountClass.Asset);
        var revenue = _f.CreateAccount(fy.Id, "3010", "Försäljning", AccountClass.Revenue);

        // Create and post an entry within the fiscal year
        var entry = new JournalEntry
        {
            Date = new DateOnly(2024, 6, 15),
            Description = "Original entry",
            FiscalYearId = fy.Id,
            Lines =
            [
                new() { AccountId = cash.Id, DebitAmount = 1000m, CreditAmount = 0 },
                new() { AccountId = revenue.Id, DebitAmount = 0, CreditAmount = 1000m }
            ]
        };
        var (created, createErr) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Null(createErr);
        Assert.NotNull(created);
        await _f.JournalEntryService.PostAsync(created.Id);

        // Act: create reversal — today is after 2024-12-31
        var (reversal, error) = await _f.JournalEntryService.CreateReversalAsync(created.Id, "Late reversal");

        // Assert: reversal should succeed and date should be clamped to FY end
        Assert.Null(error);
        Assert.NotNull(reversal);
        Assert.True(reversal.Date <= fy.EndDate,
            $"Reversal date {reversal.Date} should not exceed FY end {fy.EndDate}");
    }

    /// <summary>
    /// When today is within the fiscal year, the reversal date should use
    /// today (or at least fall within the FY boundaries).
    /// 
    /// NOTE: Uses a fiscal year that spans a very wide range to ensure today
    /// always falls within it.
    /// </summary>
    [Fact]
    public async Task Reversal_UsesToday_WhenWithinFiscalYear()
    {
        // Arrange: fiscal year that includes today
        var today = DateOnly.FromDateTime(DateTime.Today);
        var fyStart = new DateOnly(today.Year, 1, 1);
        var fyEnd = new DateOnly(today.Year, 12, 31);

        var fy = _f.CreateFiscalYear(today.Year.ToString(), fyStart, fyEnd);
        var cash = _f.CreateAccount(fy.Id, "1910", "Kassa", AccountClass.Asset);
        var revenue = _f.CreateAccount(fy.Id, "3010", "Försäljning", AccountClass.Revenue);

        await _f.CreateAndPostEntryAsync(fy.Id, cash.Id, revenue.Id, 2000m, date: fyStart);

        // Act
        var (reversal, error) = await _f.JournalEntryService.CreateReversalAsync(1, "Timely reversal");

        // Assert: reversal date should be within the fiscal year
        Assert.Null(error);
        Assert.NotNull(reversal);
        Assert.True(reversal.Date >= fy.StartDate && reversal.Date <= fy.EndDate,
            $"Reversal date {reversal.Date} should be within FY {fy.StartDate}–{fy.EndDate}");
    }
}
