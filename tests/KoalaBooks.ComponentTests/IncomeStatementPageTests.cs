using KoalaBooks.Application.Services;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.ComponentTests;

// Regression coverage for #306: IncomeStatement must seed from and write back to the
// FiscalYearSelectionContext shared with the other report/journal pages. Unlike the other
// report pages, selecting a year here does NOT auto-reload the report - the user still has
// to click "Generera" (so they can pick a date range first) - so that's asserted separately
// rather than assumed.
public class IncomeStatementPageTests : BunitContext
{
    private readonly IJournalEntryReportingService _reportingService = Substitute.For<IJournalEntryReportingService>();
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly FiscalYearSelectionContext _selectionContext = new();

    private static readonly FiscalYear ClosedFy2025 = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), IsClosed = true };
    private static readonly FiscalYear OpenFy2026 = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), IsClosed = false };

    public IncomeStatementPageTests()
    {
        _fiscalYearService.GetAllAsync().Returns([ClosedFy2025, OpenFy2026]);
        _reportingService.GetIncomeStatementAsync(Arg.Any<int>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>())
            .Returns(([], 0m));
        Services.AddSingleton(_reportingService);
        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_selectionContext);
    }

    [Fact]
    public async Task SeedsFromSharedSelection_WhenPresentInFiscalYearList()
    {
        _selectionContext.Set(ClosedFy2025.Id);

        Render<IncomeStatement>();

        await _reportingService.Received(1).GetIncomeStatementAsync(ClosedFy2025.Id, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>());
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFy2026);

        Render<IncomeStatement>();

        await _reportingService.Received(1).GetIncomeStatementAsync(OpenFy2026.Id, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>());
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection_AndGenerateUsesNewYear()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFy2026);
        var cut = Render<IncomeStatement>();

        cut.Find("select").Change(ClosedFy2025.Id.ToString());
        Assert.Equal(ClosedFy2025.Id, _selectionContext.LastSelectedFiscalYearId);

        cut.Find("button.btn").Click();

        await _reportingService.Received(1).GetIncomeStatementAsync(ClosedFy2025.Id, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>());
    }
}
