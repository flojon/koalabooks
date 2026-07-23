using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Closes the #308 zero-component-test-coverage gap for BankImport (route "/import/bank").
public class BankImportPageTests : BunitContext
{
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly IBankImportService _bankImportService = Substitute.For<IBankImportService>();
    private readonly IAccountService _accountService = Substitute.For<IAccountService>();
    private readonly IJournalEntryService _journalEntryService = Substitute.For<IJournalEntryService>();
    private readonly FiscalYearSelectionContext _selectionContext = new();

    private static readonly FiscalYear OpenFyOlder = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), IsClosed = false };
    private static readonly FiscalYear OpenFyNewer = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), IsClosed = false };

    public BankImportPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        // GetOpenFiscalYearsAsync is already ordered by StartDate descending by the real service.
        _fiscalYearService.GetOpenFiscalYearsAsync().Returns([OpenFyNewer, OpenFyOlder]);
        _bankImportService.GetImportableAccountsAsync(Arg.Any<int>(), Arg.Any<string>()).Returns([]);
        _accountService.GetAllAsync(Arg.Any<int>()).Returns([]);

        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_bankImportService);
        Services.AddSingleton(_accountService);
        Services.AddSingleton(_journalEntryService);
        Services.AddSingleton(_selectionContext);
    }

    [Fact]
    public async Task SeedsFromSharedSelection_EvenWhenNotTheDefault()
    {
        _selectionContext.Set(OpenFyOlder.Id);
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);

        Render<BankImport>();

        await _bankImportService.Received(1).GetImportableAccountsAsync(OpenFyOlder.Id, "19");
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyOlder);

        Render<BankImport>();

        await _bankImportService.Received(1).GetImportableAccountsAsync(OpenFyOlder.Id, "19");
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);
        var cut = Render<BankImport>();

        cut.Find("select").Change(OpenFyOlder.Id.ToString());

        Assert.Equal(OpenFyOlder.Id, _selectionContext.LastSelectedFiscalYearId);
        await _bankImportService.Received(1).GetImportableAccountsAsync(OpenFyOlder.Id, "19");
    }
}
