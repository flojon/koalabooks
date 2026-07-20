using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Regression coverage for #306: Journal (Verifikationer) must seed from and write back to
// the FiscalYearSelectionContext shared with the other report pages.
public class JournalPageTests : BunitContext
{
    private readonly IJournalEntryService _journalEntryService = Substitute.For<IJournalEntryService>();
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly IAccountService _accountService = Substitute.For<IAccountService>();
    private readonly ISupplierInvoiceService _invoiceService = Substitute.For<ISupplierInvoiceService>();
    private readonly IDocumentService _documentService = Substitute.For<IDocumentService>();
    private readonly FiscalYearSelectionContext _selectionContext = new();

    private static readonly FiscalYear ClosedFy2025 = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), IsClosed = true };
    private static readonly FiscalYear OpenFy2026 = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), IsClosed = false };

    public JournalPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        _fiscalYearService.GetAllAsync().Returns([ClosedFy2025, OpenFy2026]);
        _accountService.GetAllAsync(Arg.Any<int>()).Returns([]);
        _journalEntryService.GetByFiscalYearAsync(Arg.Any<int>()).Returns([]);
        _invoiceService.GetLinkedJournalEntryIdsAsync(Arg.Any<int>()).Returns([]);
        _invoiceService.GetSuppliersAsync(Arg.Any<int>()).Returns([]);
        _documentService.GetCountsForJournalEntriesAsync(Arg.Any<IEnumerable<int>>()).Returns(new Dictionary<int, int>());

        Services.AddSingleton(_journalEntryService);
        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_accountService);
        Services.AddSingleton(_invoiceService);
        Services.AddSingleton(_documentService);
        Services.AddSingleton(Substitute.For<IDocumentProvider>());
        Services.AddSingleton(_selectionContext);
    }

    [Fact]
    public async Task SeedsFromSharedSelection_WhenPresentInFiscalYearList()
    {
        _selectionContext.Set(ClosedFy2025.Id);

        Render<Journal>();

        await _journalEntryService.Received(1).GetByFiscalYearAsync(ClosedFy2025.Id);
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFy2026);

        Render<Journal>();

        await _journalEntryService.Received(1).GetByFiscalYearAsync(OpenFy2026.Id);
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFy2026);
        var cut = Render<Journal>();

        cut.Find("select").Change(ClosedFy2025.Id.ToString());

        Assert.Equal(ClosedFy2025.Id, _selectionContext.LastSelectedFiscalYearId);
        await _journalEntryService.Received(1).GetByFiscalYearAsync(ClosedFy2025.Id);
    }
}
