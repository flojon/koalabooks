using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Closes the #308 zero-component-test-coverage gap for Accounts, and covers the extraction's
// intentional behavior change: Accounts now falls back to the first open fiscal year when
// there is no shared selection and no default fiscal year set (previously it showed nothing).
public class AccountsPageTests : BunitContext
{
    private readonly IAccountService _accountService = Substitute.For<IAccountService>();
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly IBasImportService _basImportService = Substitute.For<IBasImportService>();
    private readonly FiscalYearSelectionContext _selectionContext = new();

    // Both open; Accounts orders its own open-year filter by StartDate descending, so
    // OpenFyNewer sorts first and OpenFyOlder sorts second.
    private static readonly FiscalYear OpenFyOlder = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), IsClosed = false };
    private static readonly FiscalYear OpenFyNewer = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), IsClosed = false };

    public AccountsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        _fiscalYearService.GetAllAsync().Returns([OpenFyOlder, OpenFyNewer]);
        _accountService.GetAllAsync(Arg.Any<int>()).Returns([]);

        Services.AddSingleton(_accountService);
        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_basImportService);
        Services.AddSingleton(_selectionContext);
    }

    [Fact]
    public async Task SeedsFromSharedSelection_EvenWhenNotTheDefault()
    {
        _selectionContext.Set(OpenFyOlder.Id);
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);

        Render<Accounts>();

        await _accountService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyOlder);

        Render<Accounts>();

        await _accountService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }

    [Fact]
    public async Task NoSharedSelectionAndNoDefault_FallsBackToFirstOpenCandidate()
    {
        // Regression for the #308 behavior change described above.
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns((FiscalYear?)null);

        Render<Accounts>();

        await _accountService.Received(1).GetAllAsync(OpenFyNewer.Id);
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);
        var cut = Render<Accounts>();

        cut.Find("select").Change(OpenFyOlder.Id.ToString());

        Assert.Equal(OpenFyOlder.Id, _selectionContext.LastSelectedFiscalYearId);
        await _accountService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }
}
