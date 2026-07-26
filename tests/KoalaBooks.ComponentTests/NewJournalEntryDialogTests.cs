using System.Linq;
using System.Reflection;
using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// MudDialog only renders its content when hosted by a real MudDialogProvider/IDialogService
// (see PreviewDocumentDialogTests.cs), so these tests open the dialog the same way
// Journal.razor does. bUnit cannot reliably drive a real IBrowserFile selection through
// MudFileUpload's wrapped <InputFile> in this codebase's test setup, so file-attachment
// scenarios below invoke the dialog's file-handling internals directly via reflection
// (OnFilesSelected/_pendingFiles) instead of simulating an <input type=file> pick. Manual
// Playwright verification still covers the real browser upload path end-to-end.
public class NewJournalEntryDialogTests : BunitContext, IAsyncLifetime
{
    private readonly IJournalEntryService _journalEntryService = Substitute.For<IJournalEntryService>();
    private readonly IDocumentService _documentService = Substitute.For<IDocumentService>();

    public Task InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public NewJournalEntryDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_journalEntryService);
        Services.AddSingleton(_documentService);
    }

    private static List<Account> MakeAccounts() =>
    [
        new() { Id = 1, AccountNumber = "1930", Name = "Företagskonto", IsActive = true },
        new() { Id = 2, AccountNumber = "4010", Name = "Inköp material", IsActive = true },
    ];

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> OpenDialogAsync()
    {
        var comp = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<NewJournalEntryDialog>
        {
            { x => x.Accounts, MakeAccounts() },
            { x => x.FiscalYearId, 7 },
        };

        IDialogReference? reference = null;
        await comp.InvokeAsync(async () =>
            reference = await dialogService.ShowAsync<NewJournalEntryDialog>("Ny verifikation", parameters, DialogDefaults.NoDismiss));

        return (comp, reference!);
    }

    private static AngleSharp.Dom.IElement FindButton(IRenderedComponent<MudDialogProvider> comp, string text) =>
        comp.FindAll("button").Single(b => b.TextContent == text);

    // Balances the two default empty lines: debit on line 0, credit on line 1.
    private static async Task BalanceLinesAsync(IRenderedComponent<MudDialogProvider> comp)
    {
        // Find the debit/credit inputs and balance them. Using input selectors with data attributes
        // or positional CSS selectors to be more resilient to component re-renders.
        var inputs = comp.FindAll("input[type=number]").ToList();
        if (inputs.Count >= 4)
        {
            // First change - line 0 debit
            await comp.InvokeAsync(() =>
            {
                comp.FindAll("input[type=number]")[0].Change("100");
            });
            // Second change - line 1 credit (re-find to avoid stale references)
            await comp.InvokeAsync(() =>
            {
                comp.FindAll("input[type=number]")[3].Change("100");
            });
        }
    }

    [Fact]
    public async Task RendersForm_WithTwoEmptyLines_AndSaveButtonsDisabled()
    {
        var (comp, _) = await OpenDialogAsync();

        Assert.NotNull(FindButton(comp, "💾 Bokför").GetAttribute("disabled"));
        Assert.NotNull(FindButton(comp, "Spara som utkast").GetAttribute("disabled"));
        Assert.Equal(2, comp.FindAll("input[type=number]").Count / 2);
    }

    [Fact]
    public async Task ExtraEmptyLine_DoesNotBlockSave_AndIsFilteredOutOnSave()
    {
        JournalEntry? captured = null;
        var created = new JournalEntry { Id = 60, EntryNumber = 16, Description = "x", FiscalYearId = 7 };
        _journalEntryService.CreateAsync(Arg.Do<JournalEntry>(e => captured = e)).Returns((created, (string?)null));
        _journalEntryService.PostAsync(60).Returns((string?)null);
        var (comp, dialogReference) = await OpenDialogAsync();
        await BalanceLinesAsync(comp);
        await comp.InvokeAsync(() => FindButton(comp, "+ Lägg till rad").Click());

        await comp.InvokeAsync(() => FindButton(comp, "💾 Bokför").Click());

        var result = await dialogReference.Result;
        Assert.False(result!.Canceled);
        Assert.Equal(2, captured!.Lines.Count);
    }

    [Fact]
    public async Task EditingDescription_MarksDialogDirty()
    {
        var (comp, _) = await OpenDialogAsync();

        Assert.False(comp.FindComponent<UnsavedChangesGuard>().Instance.IsDirty);

        comp.Find("input[placeholder='Beskrivning av transaktion']").Change("Kontorsmaterial");

        Assert.True(comp.FindComponent<UnsavedChangesGuard>().Instance.IsDirty);
    }

    [Fact]
    public async Task ClickingBokfor_WhenCreateFails_KeepsDialogOpen_AndShowsError()
    {
        _journalEntryService.CreateAsync(Arg.Any<JournalEntry>())
            .Returns(((JournalEntry?)null, "Kunde inte skapa verifikationen."));
        var (comp, dialogReference) = await OpenDialogAsync();
        await BalanceLinesAsync(comp);

        await comp.InvokeAsync(() => FindButton(comp, "💾 Bokför").Click());

        Assert.False(dialogReference.Result.IsCompleted);
        Assert.Contains("Kunde inte skapa verifikationen.", comp.Markup);
    }

    [Fact]
    public async Task ClickingBokfor_WhenCreateAndPostSucceed_ClosesWithResult()
    {
        var created = new JournalEntry { Id = 55, EntryNumber = 12, Description = "x", FiscalYearId = 7 };
        _journalEntryService.CreateAsync(Arg.Any<JournalEntry>()).Returns((created, (string?)null));
        _journalEntryService.PostAsync(55).Returns((string?)null);
        var (comp, dialogReference) = await OpenDialogAsync();
        await BalanceLinesAsync(comp);

        await comp.InvokeAsync(() => FindButton(comp, "💾 Bokför").Click());

        var result = await dialogReference.Result;
        Assert.False(result!.Canceled);
        var data = Assert.IsType<NewJournalEntryDialog.NewEntryResult>(result.Data);
        Assert.Same(created, data.Entry);
        Assert.True(data.Posted);
        Assert.Empty(data.FailedFiles);
        await _documentService.DidNotReceive().UploadAndLinkAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Func<Stream>>(), Arg.Any<DocumentEntityType>(), Arg.Any<int>());
    }

    [Fact]
    public async Task ClickingSparaSomUtkast_WhenCreateSucceeds_ClosesWithPostedFalse()
    {
        var created = new JournalEntry { Id = 56, EntryNumber = 13, Description = "x", FiscalYearId = 7 };
        _journalEntryService.CreateAsync(Arg.Any<JournalEntry>()).Returns((created, (string?)null));
        var (comp, dialogReference) = await OpenDialogAsync();
        await BalanceLinesAsync(comp);

        await comp.InvokeAsync(() => FindButton(comp, "Spara som utkast").Click());

        var result = await dialogReference.Result;
        var data = Assert.IsType<NewJournalEntryDialog.NewEntryResult>(result!.Data);
        Assert.False(data.Posted);
        await _journalEntryService.DidNotReceive().PostAsync(Arg.Any<int>());
    }

    [Fact]
    public async Task ClickingBokfor_WhenPostFailsThenRetried_DoesNotRecreateTheEntry()
    {
        var created = new JournalEntry { Id = 58, EntryNumber = 15, Description = "x", FiscalYearId = 7 };
        _journalEntryService.CreateAsync(Arg.Any<JournalEntry>()).Returns((created, (string?)null));
        _journalEntryService.PostAsync(58).Returns("Kunde inte bokföra.", (string?)null);
        var (comp, dialogReference) = await OpenDialogAsync();
        await BalanceLinesAsync(comp);

        await comp.InvokeAsync(() => FindButton(comp, "💾 Bokför").Click());
        Assert.False(dialogReference.Result.IsCompleted);
        Assert.Contains("Kunde inte bokföra.", comp.Markup);

        await comp.InvokeAsync(() => FindButton(comp, "💾 Bokför").Click());

        var result = await dialogReference.Result;
        Assert.False(result!.Canceled);
        await _journalEntryService.Received(1).CreateAsync(Arg.Any<JournalEntry>());
        await _journalEntryService.Received(2).PostAsync(58);
    }

    [Fact]
    public async Task SelectingSameFileTwice_DoesNotDuplicateInPendingList()
    {
        var (comp, _) = await OpenDialogAsync();
        var file = new FakeBrowserFile("kvitto.png", 1024);

        // Simulates FilesChanged firing with an overlapping/duplicate set — whether MudFileUpload
        // re-emits the full accumulated selection or just the delta on a repeated pick.
        await SelectFilesAsync(comp, file, file);
        await SelectFilesAsync(comp, file);

        Assert.Single(GetPendingFiles(comp));
    }

    [Fact]
    public async Task ClickingBokfor_WithOversizedFile_SkipsUploadAndReportsAsFailed()
    {
        var created = new JournalEntry { Id = 57, EntryNumber = 14, Description = "x", FiscalYearId = 7 };
        _journalEntryService.CreateAsync(Arg.Any<JournalEntry>()).Returns((created, (string?)null));
        _journalEntryService.PostAsync(57).Returns((string?)null);
        var (comp, dialogReference) = await OpenDialogAsync();
        await BalanceLinesAsync(comp);
        var tooLarge = new FakeBrowserFile("stor-fil.pdf", 11 * 1024 * 1024);
        await SelectFilesAsync(comp, tooLarge);

        await comp.InvokeAsync(() => FindButton(comp, "💾 Bokför").Click());

        var result = await dialogReference.Result;
        var data = Assert.IsType<NewJournalEntryDialog.NewEntryResult>(result!.Data);
        Assert.Contains("stor-fil.pdf", data.FailedFiles);
        await _documentService.DidNotReceive().UploadAndLinkAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Func<Stream>>(), Arg.Any<DocumentEntityType>(), Arg.Any<int>());
    }

    private static async Task SelectFilesAsync(IRenderedComponent<MudDialogProvider> comp, params IBrowserFile[] files)
    {
        var dialog = comp.FindComponent<NewJournalEntryDialog>().Instance;
        var method = typeof(NewJournalEntryDialog).GetMethod("OnFilesSelected", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await comp.InvokeAsync(() => method.Invoke(dialog, [(IReadOnlyList<IBrowserFile>)files]));
    }

    private static List<IBrowserFile> GetPendingFiles(IRenderedComponent<MudDialogProvider> comp)
    {
        var dialog = comp.FindComponent<NewJournalEntryDialog>().Instance;
        var field = typeof(NewJournalEntryDialog).GetField("_pendingFiles", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (List<IBrowserFile>)field.GetValue(dialog)!;
    }

    private sealed class FakeBrowserFile(string name, long size) : IBrowserFile
    {
        public string Name { get; } = name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size { get; } = size;
        public string ContentType => "application/octet-stream";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (Size > maxAllowedSize)
                throw new IOException("Supplied file exceeds the maximum allowed size.");
            return new MemoryStream(new byte[Size]);
        }
    }
}
