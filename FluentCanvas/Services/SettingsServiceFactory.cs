namespace FluentCanvas.Services;

internal static class SettingsServiceFactory
{
    internal static LocalSettingsService Create() =>
        App.IsPackaged ? new WinRTSettingsService() : new LocalSettingsService();
}
