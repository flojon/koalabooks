using KoalaBooks.Application.Services;
using KoalaBooks.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Reproduces #251: the toast shown when a background zip import finishes must name
// the zip file and report the imported count as "xx dokument importerades" - before
// this it only said "Import klar: xx importerade" with no way to tell which upload
// (if several were in flight) the toast referred to.
public class InboxZipImportToastTests : BunitContext
{
    private readonly IDocumentService _documentService = Substitute.For<IDocumentService>();

    public InboxZipImportToastTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_documentService);
        Services.AddSingleton(Substitute.For<IDocumentProvider>());

        _documentService.GetPendingAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns([]);
        _documentService.GetPendingCountAsync(Arg.Any<string?>()).Returns(0);
    }

    [Fact]
    public async Task FinishedZipBatch_ShowsToastNamingTheZipFileAndImportedCount()
    {
        _documentService.GetOpenZipBatchesAsync().Returns([
            new ZipBatchStatus { Id = 1, FileName = "fakturor.zip", ImportedCount = 3, Done = true }
        ]);

        var snackbarProvider = Render<MudSnackbarProvider>();
        await snackbarProvider.InvokeAsync(() => Render<Inbox>());

        snackbarProvider.WaitForAssertion(() =>
            Assert.Contains("fakturor.zip: 3 dokument importerades", snackbarProvider.Markup));
        _ = _documentService.Received(1).AcknowledgeZipBatchAsync(1);
    }
}
