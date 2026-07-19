using System.Text.Json;
using KoalaBooks.Application.Services;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly IBackgroundJobRunService _backgroundJobRunService = Substitute.For<IBackgroundJobRunService>();

    public InboxZipImportToastTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_documentService);
        Services.AddSingleton(_backgroundJobRunService);
        Services.AddSingleton(Substitute.For<IDocumentProvider>());
        Services.AddSingleton(Substitute.For<ILogger<KoalaBooks.Components.Shared.BackgroundJobStatusPoller>>());

        _documentService.GetPendingAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns([]);
        _documentService.GetPendingCountAsync(Arg.Any<string?>()).Returns(0);
    }

    [Fact]
    public async Task FinishedZipImportRun_ShowsToastNamingTheZipFileAndImportedCount()
    {
        var resultJson = JsonSerializer.Serialize(new
        {
            FileName = "fakturor.zip",
            ImportedCount = 3,
            SkippedCount = 0,
            SkippedReasons = Array.Empty<object>()
        });
        _backgroundJobRunService.GetOpenRunsAsync(BackgroundJobType.ZipImport).Returns([
            new BackgroundJobRun
            {
                Id = 1,
                JobType = BackgroundJobType.ZipImport,
                Status = BackgroundJobStatus.Completed,
                ResultJson = resultJson,
                CreatedAt = DateTime.UtcNow
            }
        ]);

        var snackbarProvider = Render<MudSnackbarProvider>();
        await snackbarProvider.InvokeAsync(() => Render<Inbox>());

        snackbarProvider.WaitForAssertion(() =>
            Assert.Contains("fakturor.zip: 3 dokument importerades", snackbarProvider.Markup));
        _ = _backgroundJobRunService.Received(1).AcknowledgeAsync(1);
    }

    // Reproduces the crash a run left Failed with no ResultJson would otherwise cause:
    // BackgroundJobRunFailureFilter (#285) only flips Status to Failed when all retries are
    // exhausted, it never writes ResultJson — a run that fails before processing its first
    // entry (e.g. the staging large object can't be read) reaches OnRunCompleted with
    // ResultJson still null.
    [Fact]
    public async Task FailedZipImportRun_WithNoResultJson_ShowsGenericFailureToastInsteadOfCrashing()
    {
        _backgroundJobRunService.GetOpenRunsAsync(BackgroundJobType.ZipImport).Returns([
            new BackgroundJobRun
            {
                Id = 1,
                JobType = BackgroundJobType.ZipImport,
                Status = BackgroundJobStatus.Failed,
                ResultJson = null,
                CreatedAt = DateTime.UtcNow
            }
        ]);

        var snackbarProvider = Render<MudSnackbarProvider>();
        await snackbarProvider.InvokeAsync(() => Render<Inbox>());

        snackbarProvider.WaitForAssertion(() =>
            Assert.Contains("Zip-import misslyckades.", snackbarProvider.Markup));
        _ = _backgroundJobRunService.Received(1).AcknowledgeAsync(1);
    }
}
