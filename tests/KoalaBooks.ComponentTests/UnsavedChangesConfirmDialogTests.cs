using System.Linq;
using KoalaBooks.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Regression coverage for the bug from PR #218: UnsavedChangesGuard originally rendered
// its confirm prompt as an inline <MudDialog Visible="true">, never opened through
// IDialogService. MudDialog only wires up its own click handling when hosted by a real
// MudDialogProvider/IDialogService (see PreviewDocumentDialogTests), so the buttons
// rendered but silently did nothing on every click - unreproducible without actually
// opening the dialog the way Inbox.razor and friends do.
public class UnsavedChangesConfirmDialogTests : BunitContext, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public UnsavedChangesConfirmDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> OpenDialogAsync()
    {
        var comp = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<UnsavedChangesConfirmDialog>
        {
            { x => x.Message, "Du har osparade ändringar." },
        };

        IDialogReference? reference = null;
        await comp.InvokeAsync(async () =>
            reference = await dialogService.ShowAsync<UnsavedChangesConfirmDialog>(null, parameters));

        return (comp, reference!);
    }

    [Fact]
    public async Task ClickingStannaKvar_ClosesWithFalse_AndDoesNotCancel()
    {
        var (comp, dialogReference) = await OpenDialogAsync();

        var stannaKvar = comp.FindAll("button").Single(b => b.TextContent == "Stanna kvar");
        await comp.InvokeAsync(() => stannaKvar.Click());

        var result = await dialogReference.Result;
        Assert.False(result!.Canceled);
        Assert.False((bool)result.Data!);
    }

    [Fact]
    public async Task ClickingLamnaSidan_ClosesWithTrue_AndDoesNotCancel()
    {
        var (comp, dialogReference) = await OpenDialogAsync();

        var lamnaSidan = comp.FindAll("button").Single(b => b.TextContent == "Lämna sidan");
        await comp.InvokeAsync(() => lamnaSidan.Click());

        var result = await dialogReference.Result;
        Assert.False(result!.Canceled);
        Assert.True((bool)result.Data!);
    }
}
