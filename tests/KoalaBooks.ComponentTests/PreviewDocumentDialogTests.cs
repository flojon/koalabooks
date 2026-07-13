using System.Linq;
using KoalaBooks.Application.Services;
using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            Substitute.For<IDocumentExtractor>(),
            Substitute.For<ICurrentUser>(),
            Substitute.For<ILogger<DocumentService>>());

        Services.AddSingleton(_documentService);
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
            .Returns((string?)null);
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
            .Returns("Dokumentet hittades inte.");
        var doc = MakeDoc();

        var (comp, dialogReference) = await OpenDialogAsync(doc);

        await comp.InvokeAsync(() => BokforButton(comp).Click());

        Assert.False(dialogReference.Result.IsCompleted);
        Assert.Contains("Dokumentet hittades inte.", comp.Markup);
    }
}
