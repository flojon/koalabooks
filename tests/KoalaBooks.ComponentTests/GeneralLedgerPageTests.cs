using KoalaBooks.Application.Services;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Regression coverage for #306: GeneralLedger (Huvudbok) must seed from and write back to
// the FiscalYearSelectionContext shared with the other report/journal pages.
public class GeneralLedgerPageTests : BunitContext
{
    private readonly IJournalEntryReportingService _reportingService = Substitute.For<IJournalEntryReportingService>();
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly IAccountService _accountService = Substitute.For<IAccountService>();
    private readonly FiscalYearSelectionContext _selectionContext = new();

    private static readonly FiscalYear ClosedFy2025 = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), IsClosed = true };
    private static readonly FiscalYear OpenFy2026 = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), IsClosed = false };

    public GeneralLedgerPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        _fiscalYearService.GetAllAsync().Returns([ClosedFy2025, OpenFy2026]);
        _accountService.GetAllAsync(Arg.Any<int>()).Returns([]);
        _reportingService.GetComputedBalancesAsync(Arg.Any<int>()).Returns(new Dictionary<int, (decimal, decimal)>());
        _reportingService.GetAccountIdsWithTransactionsAsync(Arg.Any<int>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<bool>())
            .Returns(new HashSet<int>());

        Services.AddSingleton(_reportingService);
        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_accountService);
        Services.AddSingleton(_selectionContext);
    }

    [Fact]
    public async Task SeedsFromSharedSelection_WhenPresentInFiscalYearList()
    {
        _selectionContext.Set(ClosedFy2025.Id);

        Render<GeneralLedger>();

        await _accountService.Received(1).GetAllAsync(ClosedFy2025.Id);
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFy2026);

        Render<GeneralLedger>();

        await _accountService.Received(1).GetAllAsync(OpenFy2026.Id);
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFy2026);
        var cut = Render<GeneralLedger>();

        cut.Find("select").Change(ClosedFy2025.Id.ToString());

        Assert.Equal(ClosedFy2025.Id, _selectionContext.LastSelectedFiscalYearId);
        await _accountService.Received(1).GetAllAsync(ClosedFy2025.Id);
    }
}
