using MudBlazor;

namespace KoalaBooks.Components.Shared;

public static class DialogDefaults
{
    public static readonly DialogOptions NoDismiss = new()
    {
        BackdropClick = false,
        CloseOnEscapeKey = false
    };
}
