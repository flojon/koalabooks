using System.Linq;
using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// MudDialog only renders its content when hosted by a real MudDialogProvider/IDialogService
// (see PreviewDocumentDialogTests.cs), so these tests open the dialog the same way
// Journal.razor does. File-attachment scenarios (staging/removing/upload-failure) are not
// covered here — bUnit cannot reliably drive a real IBrowserFile selection through
// MudFileUpload's wrapped <InputFile> in this codebase's test setup; that's covered by
// manual Playwright verification instead (see the plan's Global Constraints).
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
}
