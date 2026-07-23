using System.Linq;
using KoalaBooks.Application.Services;
using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// DocumentService is a concrete, DB-backed class; the whole test suite otherwise runs it
// against a real Postgres container (see KoalaBooks.Tests/TestFixture). That's too heavy
// for a component test, so UpdateMetadataAsync is marked virtual and substituted here via
// Substitute.ForPartsOf - the dummy db/storage/extractor are never touched because the one
// method that would touch them is overridden.
//
// MudDialog only renders its content when hosted by a real MudDialogProvider/IDialogService
// (it checks an internal cascading dialog-instance parameter that can't be satisfied by hand),
// so these tests open the dialog the same way Inbox.razor does.
public class PreviewDocumentDialogTests : BunitContext, IAsyncLifetime
{
    private readonly DocumentService _documentService;
    private readonly IDocumentProvider _documentProvider = Substitute.For<IDocumentProvider>();

    // MudDialogProvider registers services (e.g. PointerEventsNoneService) that only implement
    // IAsyncDisposable; xunit's synchronous IDisposable.Dispose (used by default) can't tear
    // those down, so route teardown through IAsyncLifetime's async DisposeAsync instead.
    public Task InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public PreviewDocumentDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().Options;
        var db = new AppDbContext(dbOptions, Substitute.For<ICurrentUser>());
        _documentService = Substitute.ForPartsOf<DocumentService>(
            db,
            Substitute.For<IDocumentStorage>(),
            Substitute.For<IDocumentExtractionQueue>(),
            Substitute.For<IZipImportQueue>(),
            Substitute.For<IBackgroundJobRunService>(),
            Substitute.For<ICurrentUser>());

        Services.AddSingleton<IDocumentService>(_documentService);

        // ClassifyDocumentDialog's other dependencies, needed only by
        // ClickingBokfor_EditedDateCarriesIntoClassifyDialog below. They must be
        // registered here, before any component renders - bUnit locks the service
        // collection against further registrations after the first resolve.
        Services.AddSingleton<ISupplierInvoiceService>(new SupplierInvoiceService(db, Substitute.For<ICurrentUser>()));
        Services.AddSingleton<ICustomerInvoiceService>(new CustomerInvoiceService(db));
        Services.AddSingleton<IAccountService>(new AccountService(db));
        Services.AddSingleton<ICustomerService>(new CustomerService(db));
        var fiscalYearService = Substitute.For<IFiscalYearService>();
        fiscalYearService.GetForDateAsync(Arg.Any<DateOnly>()).Returns((FiscalYear?)null);
        Services.AddSingleton(fiscalYearService);
        Services.AddSingleton(Substitute.For<IJournalEntryService>());
    }

    private static DocumentMeta MakeDoc() => new()
    {
        Id = 42,
        FileName = "faktura.pdf",
        ContentType = "application/pdf",
        FileSize = 1234,
        ClassifiedType = null,
        DocumentDate = new DateOnly(2026, 1, 10),
    };

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> OpenDialogAsync(DocumentMeta doc)
    {
        var comp = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<PreviewDocumentDialog>
        {
            { x => x.Doc, doc },
            { x => x.DocumentProvider, _documentProvider },
        };

        IDialogReference? reference = null;
        await comp.InvokeAsync(async () =>
            reference = await dialogService.ShowAsync<PreviewDocumentDialog>("Förhandsgranskning", parameters));

        return (comp, reference!);
    }

    private static AngleSharp.Dom.IElement BokforButton(IRenderedComponent<MudDialogProvider> comp) =>
        comp.FindAll("button.btn-secondary").Single(b => b.TextContent == "Bokför");

    [Fact]
    public async Task ClickingBokfor_PersistsEditedFields_BeforeClosing()
    {
        _documentService.UpdateMetadataAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateOnly?>())
            .Returns((true, (string?)null));
        var doc = MakeDoc();

        var (comp, dialogReference) = await OpenDialogAsync(doc);

        comp.Find("select").Change("CustomerInvoice");
        comp.Find("input").Change("2026-03-15");
        await comp.InvokeAsync(() => BokforButton(comp).Click());

        var result = await dialogReference.Result;

        _ = _documentService.Received(1).UpdateMetadataAsync(42, "CustomerInvoice", new DateOnly(2026, 3, 15));
        Assert.Equal("CustomerInvoice", doc.ClassifiedType);
        Assert.Equal(new DateOnly(2026, 3, 15), doc.DocumentDate);
        Assert.False(result!.Canceled);
        Assert.Equal(PreviewDocumentDialog.PreviewOutcome.Classify, (PreviewDocumentDialog.PreviewOutcome)result.Data!);
    }

    [Fact]
    public async Task ClickingBokfor_WhenSaveFails_DoesNotCloseDialog()
    {
        _documentService.UpdateMetadataAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateOnly?>())
            .Returns((false, "Dokumentet hittades inte."));
        var doc = MakeDoc();

        var (comp, dialogReference) = await OpenDialogAsync(doc);

        await comp.InvokeAsync(() => BokforButton(comp).Click());

        Assert.False(dialogReference.Result.IsCompleted);
        Assert.Contains("Dokumentet hittades inte.", comp.Markup);
    }

    // Reproduces the bug from #223: editing the date in the preview and clicking
    // "Bokför" must carry that edit into ClassifyDocumentDialog, the same way
    // Inbox.razor chains the two dialogs by passing along the same DocumentMeta.
    //
    // Both dialogs are shown through the same MudDialogProvider/IDialogService pair,
    // matching how the real app only ever mounts one MudDialogProvider (at the layout
    // root). MudDialogProvider adds the new dialog to its render tree via an
    // OnDialogInstanceAdded event handler dispatched through the renderer, which isn't
    // guaranteed to have flushed by the time the outer InvokeAsync call returns - this
    // was observed to pass consistently locally but fail intermittently in CI, so the
    // final assertion polls via WaitForAssertion instead of assuming the DOM already
    // reflects the new dialog.
    [Fact]
    public async Task ClickingBokfor_EditedDateCarriesIntoClassifyDialog()
    {
        _documentService.UpdateMetadataAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateOnly?>())
            .Returns((true, (string?)null));
        var doc = MakeDoc();

        var (comp, previewReference) = await OpenDialogAsync(doc);
        comp.Find("select").Change("CustomerInvoice");
        comp.Find("input").Change("2026-03-15");
        await comp.InvokeAsync(() => BokforButton(comp).Click());
        var previewResult = await previewReference.Result;
        Assert.Equal(PreviewDocumentDialog.PreviewOutcome.Classify, (PreviewDocumentDialog.PreviewOutcome)previewResult!.Data!);

        var dialogService = Services.GetRequiredService<IDialogService>();
        var classifyParameters = new DialogParameters<ClassifyDocumentDialog>
        {
            { x => x.Doc, doc },
            { x => x.DocumentProvider, _documentProvider },
        };
        await comp.InvokeAsync(async () =>
            await dialogService.ShowAsync<ClassifyDocumentDialog>("Klassificera dokument", classifyParameters));

        // doc.ClassifiedType == "CustomerInvoice" (persisted above), so that branch's
        // date inputs are the ones rendered by default; the first is Fakturadatum (_date).
        comp.WaitForAssertion(
            () => Assert.Equal("2026-03-15", comp.FindAll("input[type=date]")[0].GetAttribute("value")),
            timeout: TimeSpan.FromSeconds(5));
    }
}
