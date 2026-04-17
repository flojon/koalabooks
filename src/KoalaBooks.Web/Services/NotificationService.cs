using MudBlazor;

namespace KoalaBooks.Web.Services;

public class NotificationService(ISnackbar snackbar)
{
    public void Success(string message) =>
        snackbar.Add(message, Severity.Success, config => { config.VisibleStateDuration = 3000; });

    public void Error(string message) =>
        snackbar.Add(message, Severity.Error, config => { config.RequireInteraction = true; });

    public void Info(string message) =>
        snackbar.Add(message, Severity.Info, config => { config.VisibleStateDuration = 4000; });

    public void Warning(string message) =>
        snackbar.Add(message, Severity.Warning, config => { config.VisibleStateDuration = 5000; });
}
