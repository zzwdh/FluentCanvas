using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using FluentCanvas.Services;
using FluentCanvas.Views;
using NativeWindow = Microsoft.UI.Xaml.Window;

namespace FluentCanvas;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Func<NativeWindow> createWindow = args switch
        {
            ["--diagnostics"] => WinUiSupport.CreateDiagnostics,
            ["--guide"] => WinUiSupport.CreateGuide,
            ["--countdown"] => WinUiClassroomTools.CreateCountdown,
            ["--random"] => () => WinUiClassroomTools.CreateRandom(false),
            ["--random-single"] => () => WinUiClassroomTools.CreateRandom(true),
            ["--winui-settings", var pipe] => () => WinUiSettings.CreateWindow(pipe),
            _ => null,
        };
        var toolLanguage = createWindow == null
            ? null
            : Environment.GetEnvironmentVariable(WinUiWindowLauncher.LanguageEnvironmentVariable);
        var preference = !string.IsNullOrEmpty(toolLanguage) && UiText.IsSupportedPreference(toolLanguage)
            ? toolLanguage
            : ReadLanguagePreference();
        UiText.Initialize(preference, WindowsDisplayLanguage());

        if (createWindow == null)
        {
            App.Main();
            return;
        }

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            ApplyNativeControlLanguage();
            Microsoft.UI.Xaml.Application.Start(parameters =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new WinUiToolApplication(createWindow);
            });
        }
        catch (Exception ex)
        {
            Helpers.LogHelper.NewLog(ex.ToString());
            System.Windows.MessageBox.Show(UiText.Get("Canvas.ToolWindowFailed"), "FluentCanvas");
            Environment.ExitCode = 1;
        }
    }

    // A packaged language override can persist across runs. Follow the user's Windows UI language independently.
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    private static CultureInfo WindowsDisplayLanguage()
    {
        try { return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()); }
        catch (CultureNotFoundException) { return CultureInfo.CurrentUICulture; }
    }

    private static void ApplyNativeControlLanguage()
    {
        var tag = UiText.ActiveCulture.Name switch
        {
            "en" => "en-US",
            "ja" => "ja-JP",
            "ha" => "ha-Latn-NG",
            "sw" => "sw-KE",
            var name => name
        };
        try
        {
            if (Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride != tag)
                Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = tag;
        }
        catch (Exception ex)
        {
            LogHelper.NewLog($"Could not set Windows control language to {tag}: {ex.Message}");
        }
    }

    private static string ReadLanguagePreference()
    {
        try
        {
            LocalSettingsService settings = SettingsServiceFactory.Create();
            return settings.ReadStoredSettings()?.Appearance?.Language;
        }
        catch (Exception)
        {
            // The canvas startup path reports unreadable settings. The tool can still use the Windows language.
            return null;
        }
    }
}

internal sealed partial class WinUiToolApplication(Func<NativeWindow> createWindow)
    : Microsoft.UI.Xaml.Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider metadata = new();
    private NativeWindow window;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        window = createWindow();
        if (window.Content is Microsoft.UI.Xaml.FrameworkElement content)
            WinUiClassroomTools.SetLanguage(content);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "FluentCanvas.ico");
        if (File.Exists(iconPath))
            window.AppWindow.SetIcon(iconPath);
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = true;
        window.Closed += (_, _) => Exit();
        window.Activate();
    }

    public IXamlType GetXamlType(Type type) => metadata.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => metadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => metadata.GetXmlnsDefinitions();
}
