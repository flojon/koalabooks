using System.Diagnostics.CodeAnalysis;

namespace KoalaBooks.Client;

// App.razor's MudPopoverProvider/MudDialogProvider/MudSnackbarProvider are only ever referenced
// via the @rendermode root-component marker, not from Client code directly, so publish trimming
// removes them and WASM root-component resolution fails with "Root component type ... could not
// be found in the assembly 'MudBlazor'". Preserve() must be called from Program.cs so the trimmer
// considers it (and its dependencies) reachable — an uncalled method would just get trimmed too.
internal static class TrimmerPreserve
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(MudBlazor.MudPopoverProvider))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(MudBlazor.MudDialogProvider))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(MudBlazor.MudSnackbarProvider))]
    public static void Preserve()
    {
    }
}
