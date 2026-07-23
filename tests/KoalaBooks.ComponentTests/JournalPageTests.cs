using KoalaBooks.Domain;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Regression coverage for #306: Journal (Verifikationer) must seed from and write back to
// the FiscalYearSelectionContext shared with the other report pages.
public class JournalPageTests : BunitContext, IAsyncLifetime
{
    // MudMenu (rendered per journal-entry row) registers services (e.g. PointerEventsNoneService)
    // that only implement IAsyncDisposable; xunit's synchronous IDisposable.Dispose (used by
    // default) can't tear those down, so route teardown through IAsyncLifetime's async DisposeAsync.
    public Task InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

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
        _journalEntryService.GetByFiscalYearAsync(
                Arg.Any<int>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<string?>(),
                Arg.Any<JournalEntrySortBy>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new PagedResult<JournalEntry> { Items = [], Page = 1, PageSize = 50, TotalCount = 0 });
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

        await _journalEntryService.Received(1).GetByFiscalYearAsync(
            ClosedFy2025.Id, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<string?>(),
            Arg.Any<JournalEntrySortBy>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFy2026);

        Render<Journal>();

        await _journalEntryService.Received(1).GetByFiscalYearAsync(
            OpenFy2026.Id, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<string?>(),
            Arg.Any<JournalEntrySortBy>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public void MonthFilterWithNoResults_ButYearHasEntries_ShowsPeriodMessage_NotYearMessage()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFy2026);

        // Unfiltered call (no date range) returns entries for the year...
        _journalEntryService.GetByFiscalYearAsync(
                OpenFy2026.Id, null, null, Arg.Any<string?>(),
                Arg.Any<JournalEntrySortBy>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new PagedResult<JournalEntry> { Items = [new JournalEntry { Id = 1, EntryNumber = 1, Description = "Test" }], Page = 1, PageSize = 50, TotalCount = 5 });

        // ...but the selected month has none.
        _journalEntryService.GetByFiscalYearAsync(
                OpenFy2026.Id, Arg.Is<DateOnly?>(d => d != null), Arg.Is<DateOnly?>(d => d != null), Arg.Any<string?>(),
                Arg.Any<JournalEntrySortBy>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new PagedResult<JournalEntry> { Items = [], Page = 1, PageSize = 50, TotalCount = 0 });

        var cut = Render<Journal>();

        var monthSelect = cut.FindAll("select")[1];
        monthSelect.Change("1");

        var alert = cut.Find(".mud-alert");
        Assert.Contains("Inga verifikationer för vald period.", alert.TextContent);
        Assert.DoesNotContain("Inga verifikationer ännu", alert.TextContent);
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFy2026);
        var cut = Render<Journal>();

        cut.Find("select").Change(ClosedFy2025.Id.ToString());

        Assert.Equal(ClosedFy2025.Id, _selectionContext.LastSelectedFiscalYearId);
        await _journalEntryService.Received(1).GetByFiscalYearAsync(
            ClosedFy2025.Id, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<string?>(),
            Arg.Any<JournalEntrySortBy>(), Arg.Any<int>(), Arg.Any<int>());
    }
}
