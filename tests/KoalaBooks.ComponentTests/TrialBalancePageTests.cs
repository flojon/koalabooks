using KoalaBooks.Application.Services;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.ComponentTests;

// Regression coverage for #306: TrialBalance must seed from and write back to the
// FiscalYearSelectionContext shared with the other report/journal pages.
public class TrialBalancePageTests : BunitContext
{
    private readonly IJournalEntryReportingService _reportingService = Substitute.For<IJournalEntryReportingService>();
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly FiscalYearSelectionContext _selectionContext = new();

    private static readonly FiscalYear ClosedFy2025 = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), IsClosed = true };
    private static readonly FiscalYear OpenFy2026 = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), IsClosed = false };

    public TrialBalancePageTests()
    {
        _fiscalYearService.GetAllAsync().Returns([ClosedFy2025, OpenFy2026]);
        _reportingService.GetTrialBalanceAsync(Arg.Any<int>()).Returns([]);
        Services.AddSingleton(_reportingService);
        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_selectionContext);
    }

    [Fact]
    public async Task SeedsFromSharedSelection_WhenPresentInFiscalYearList()
    {
        _selectionContext.Set(ClosedFy2025.Id);

        Render<TrialBalance>();

        await _reportingService.Received(1).GetTrialBalanceAsync(ClosedFy2025.Id);
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFy2026);

        Render<TrialBalance>();

        await _reportingService.Received(1).GetTrialBalanceAsync(OpenFy2026.Id);
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFy2026);
        var cut = Render<TrialBalance>();

        cut.Find("select").Change(ClosedFy2025.Id.ToString());

        Assert.Equal(ClosedFy2025.Id, _selectionContext.LastSelectedFiscalYearId);
        await _reportingService.Received(1).GetTrialBalanceAsync(ClosedFy2025.Id);
    }
}
