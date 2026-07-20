using System.Text.Json;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using Microsoft.AspNetCore.Components.Forms;
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
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();

    public InboxZipImportToastTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_documentService);
        Services.AddSingleton(_backgroundJobRunService);
        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(Substitute.For<IDocumentProvider>());
        Services.AddSingleton(Substitute.For<ILogger<KoalaBooks.Components.Shared.BackgroundJobStatusPoller>>());

        _fiscalYearService.GetOpenFiscalYearsAsync().Returns([]);
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

    // The poller's one poll at page load runs before the zip job exists, so it finds zero
    // open runs and never arms its timer (see
    // BackgroundJobStatusPollerTests.NoOpenRuns_CallsGetOpenRunsOnceOnInit). Without a kick
    // after upload, completion stays invisible until a full page reload.
    [Fact]
    public async Task ZipAccepted_ImmediatelyPollsSoCompletionIsNoticedWithoutAReload()
    {
        _backgroundJobRunService.GetOpenRunsAsync(BackgroundJobType.ZipImport).Returns([]);
        _documentService.UploadZipAsync("fakturor.zip", Arg.Any<Func<Stream>>())
            .Returns((1, (string?)null));

        var file = Substitute.For<IBrowserFile>();
        file.Name.Returns("fakturor.zip");
        file.Size.Returns(1024L);

        var snackbarProvider = Render<MudSnackbarProvider>();
        var cut = await snackbarProvider.InvokeAsync(() => Render<Inbox>());

        _ = _backgroundJobRunService.Received(1).GetOpenRunsAsync(BackgroundJobType.ZipImport);

        var upload = cut.FindComponent<MudFileUpload<IReadOnlyList<IBrowserFile>>>();
        await cut.InvokeAsync(() => upload.Instance.FilesChanged.InvokeAsync(new List<IBrowserFile> { file }));

        _ = _backgroundJobRunService.Received(2).GetOpenRunsAsync(BackgroundJobType.ZipImport);
    }
}
