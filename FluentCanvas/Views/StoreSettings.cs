using FluentCanvas.Localization;
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentCanvas.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Services.Store;

namespace FluentCanvas.Views;

internal static class StoreSettings
{
    internal static FrameworkElement CreateSection(Window window, string pipeName, CancellationToken cancellation)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Microsoft Store", FontSize = 20 });

        if (!StoreUpdateService.IsStoreSigned)
        {
            panel.Children.Add(new TextBlock
            {
                Text = App.IsPackaged
                    ? UiText.Get("Settings.StoreNonStorePackage")
                    : UiText.Get("Settings.StoreStandalone"),
                TextWrapping = TextWrapping.Wrap,
            });
            var unavailable = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            unavailable.Children.Add(new Button { Content = UiText.Get("Settings.StoreRate"), IsEnabled = false });
            unavailable.Children.Add(new Button { Content = UiText.Get("Settings.StoreCheck"), IsEnabled = false });
            panel.Children.Add(unavailable);
            return panel;
        }

        var state = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var autoDownload = new ToggleSwitch
        {
            Header = UiText.Get("Settings.StoreAutoDownload"),
            OnContent = UiText.Get("Settings.On"),
            OffContent = UiText.Get("Settings.Off"),
        };
        try { autoDownload.IsOn = StoreUpdateService.AutoDownload; }
        catch (Exception ex) { autoDownload.IsEnabled = false; state.Text = ErrorText(ex); }
        autoDownload.Toggled += (_, _) =>
        {
            if (!autoDownload.IsEnabled) return;
            try { StoreUpdateService.AutoDownload = autoDownload.IsOn; }
            catch (Exception ex) { state.Text = ErrorText(ex); autoDownload.IsEnabled = false; }
        };
        panel.Children.Add(autoDownload);
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("Settings.StoreDownloadHint"),
            TextWrapping = TextWrapping.Wrap,
        });

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var rate = new Button { Content = UiText.Get("Settings.StoreRate") };
        var check = new Button { Content = UiText.Get("Settings.StoreCheck") };
        controls.Children.Add(rate);
        controls.Children.Add(check);
        panel.Children.Add(controls);
        panel.Children.Add(state);

        StoreContext OpenStore() => StoreUpdateService.CreateContext(WinRT.Interop.WindowNative.GetWindowHandle(window));

        async Task RunAsync(Func<Task> action)
        {
            rate.IsEnabled = false;
            check.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { state.Text = ErrorText(ex); }
            finally
            {
                if (!cancellation.IsCancellationRequested) { rate.IsEnabled = true; check.IsEnabled = true; }
            }
        }

        rate.Click += async (_, _) => await RunAsync(async () =>
        {
            state.Text = UiText.Get("Settings.StoreOpeningRating");
            var result = await OpenStore().RequestRateAndReviewAppAsync();
            state.Text = result.ExtendedError != null
                ? ErrorText(result.ExtendedError)
                : result.WasUpdated ? UiText.Get("Settings.StoreRatingUpdated") : UiText.Get("Settings.StoreRatingClosed");
        });

        check.Click += async (_, _) => await RunAsync(async () =>
        {
            state.Text = UiText.Get("Settings.StoreChecking");
            var context = OpenStore();
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates.Count == 0)
            {
                state.Text = UiText.Get("Settings.StoreNoUpdate");
                return;
            }

            state.Text = UiText.Get("Settings.StoreAvailable");
            var confirm = new ContentDialog
            {
                XamlRoot = panel.XamlRoot,
                Title = UiText.Get("Settings.StoreInstallConfirmation"),
                Content = UiText.Get("Settings.StoreInstallHint"),
                PrimaryButtonText = UiText.Get("Settings.StoreBackupAndUpdate"),
                CloseButtonText = UiText.Get("Settings.StoreLater"),
                DefaultButton = ContentDialogButton.Close,
            };
            WinUiClassroomTools.SetLanguage(confirm);
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                state.Text = UiText.Get("Settings.StoreDeferred");
                return;
            }

            try
            {
                state.Text = UiText.Get("Settings.StoreBackingUp");
                var preparation = await SettingsPipe.RequestAsync(pipeName,
                    new(WinUiSetting.PrepareStoreUpdate), cancellation);
                if (preparation.Error != null)
                {
                    state.Text = preparation.Error;
                    return;
                }

                state.Text = UiText.Get("Settings.StoreRequesting");
                bool updating = true;
                var progress = new Progress<StorePackageUpdateStatus>(status =>
                {
                    if (!updating || cancellation.IsCancellationRequested) return;
                    state.Text = status.PackageUpdateState switch
                    {
                        StorePackageUpdateState.Downloading => UiText.Format("Settings.StoreDownloading", status.TotalDownloadProgress),
                        StorePackageUpdateState.Deploying => UiText.Get("Settings.StoreInstalling"),
                        StorePackageUpdateState.Completed => UiText.Get("Settings.StoreFinishing"),
                        _ => state.Text,
                    };
                });
                StorePackageUpdateResult result;
                try
                {
                    result = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates).AsTask(cancellation, progress);
                }
                finally { updating = false; }
                state.Text = result.OverallState switch
                {
                    StorePackageUpdateState.Completed => UiText.Get("Settings.StoreCompleted"),
                    StorePackageUpdateState.Canceled => UiText.Get("Settings.StoreCanceled"),
                    StorePackageUpdateState.ErrorLowBattery => UiText.Get("Settings.StoreLowBattery"),
                    StorePackageUpdateState.ErrorWiFiRecommended or StorePackageUpdateState.ErrorWiFiRequired => UiText.Get("Settings.StoreNeedsWifi"),
                    _ => UiText.Format("Settings.StoreUpdateFailed", result.OverallState),
                };
                if (preparation.BackupPath != null)
                    state.Text += UiText.Format("Settings.StoreBackupLocation", preparation.BackupPath);
            }
            finally
            {
                if (!cancellation.IsCancellationRequested)
                {
                    try { await SettingsPipe.RequestAsync(pipeName, new(WinUiSetting.ReleaseStoreUpdate), cancellation); }
                    catch (Exception) when (!cancellation.IsCancellationRequested)
                    { state.Text += UiText.Get("Settings.StoreResumeEditing"); }
                }
            }
        });
        return panel;
    }

    private static string ErrorText(Exception error) =>
        UiText.Format("Settings.StoreRequestFailed", error.HResult);
}
