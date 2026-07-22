using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Closes the #308 zero-component-test-coverage gap for SupplierInvoices.
public class SupplierInvoicesPageTests : BunitContext
{
    private readonly ISupplierInvoiceService _invoiceService = Substitute.For<ISupplierInvoiceService>();
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly IAccountService _accountService = Substitute.For<IAccountService>();
    private readonly IDocumentService _documentService = Substitute.For<IDocumentService>();
    private readonly IDocumentProvider _documentProvider = Substitute.For<IDocumentProvider>();
    private readonly FiscalYearSelectionContext _selectionContext = new();

    private static readonly FiscalYear OpenFyOlder = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), IsClosed = false };
    private static readonly FiscalYear OpenFyNewer = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), IsClosed = false };

    public SupplierInvoicesPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        _fiscalYearService.GetOpenFiscalYearsAsync().Returns([OpenFyNewer, OpenFyOlder]);
        _accountService.GetAllAsync(Arg.Any<int>()).Returns([]);
        _invoiceService.GetAllAsync(Arg.Any<int>()).Returns([]);
        _invoiceService.GetSuppliersAsync(Arg.Any<int>()).Returns([]);

        Services.AddSingleton(_invoiceService);
        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_accountService);
        Services.AddSingleton(_documentService);
        Services.AddSingleton(_documentProvider);
        Services.AddSingleton(_selectionContext);
    }

    [Fact]
    public async Task SeedsFromSharedSelection_EvenWhenNotTheDefault()
    {
        _selectionContext.Set(OpenFyOlder.Id);
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);

        Render<SupplierInvoices>();

        await _invoiceService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyOlder);

        Render<SupplierInvoices>();

        await _invoiceService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);
        var cut = Render<SupplierInvoices>();

        cut.Find("select").Change(OpenFyOlder.Id.ToString());

        Assert.Equal(OpenFyOlder.Id, _selectionContext.LastSelectedFiscalYearId);
        await _invoiceService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }
}
