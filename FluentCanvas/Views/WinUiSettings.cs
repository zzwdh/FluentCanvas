using FluentCanvas.Localization;
using FluentCanvas.Helpers;
using FluentCanvas.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace FluentCanvas.Views;

internal static class WinUiSettings
{
    internal static Window CreateWindow(string pipeName) => new SettingsWindow(pipeName).Window;

    private sealed class SettingsWindow
    {
        private readonly string pipeName;
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<Action<WinUiSettingsState>> render = new();
        private readonly List<Control> editors = new();
        private readonly Dictionary<string, (TextBox Input, TextBlock Status, string Gesture)> hotkeyRows = new();
        private readonly StackPanel settings = new() { Padding = new Thickness(24), Spacing = 22 };
        private readonly InfoBar notice = new() { IsClosable = true };
        private ToggleSwitch startupToggle;
        private TextBlock startupHint;
        private readonly ToggleSwitch multiTouch;
        private readonly NumberBox retentionDays;
        private WinUiSettingsState state;
        private bool busy;
        private bool closed;
        private bool refreshStartupOnActivation;

        internal Window Window { get; } = new() { Title = UiText.Get("Settings.WindowTitle") };

        internal SettingsWindow(string pipeName)
        {
            this.pipeName = pipeName;
            var retry = new Button { Content = UiText.Get("Settings.Reload") };
            retry.Click += async (_, _) => await ChangeAsync(new(WinUiSetting.Read));
            notice.ActionButton = retry;

            var appearance = AddSection(UiText.Get("Settings.Appearance"));
            var language = new ComboBox
            {
                Header = UiText.Get("Settings.Language"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetAutomationId(language, nameof(WinUiSetting.Language));
            AutomationProperties.SetName(language, UiText.Get("Settings.Language"));
            var languageItems = new Dictionary<string, ComboBoxItem>();
            foreach (var option in UiText.Languages)
            {
                var item = new ComboBoxItem { Content = option.Name, Tag = option.Code };
                languageItems.Add(option.Code, item);
                language.Items.Add(item);
            }
            language.SelectionChanged += async (_, _) =>
            {
                if (language.SelectedItem is ComboBoxItem { Tag: string code })
                    await ChangeAsync(new(WinUiSetting.Language, Text: code));
            };
            appearance.Children.Add(language);
            appearance.Children.Add(new TextBlock
            {
                Text = UiText.Get("Settings.LanguageRestart"),
                TextWrapping = TextWrapping.Wrap,
            });
            editors.Add(language);
            render.Add(value => language.SelectedItem = UiText.IsSupportedPreference(value.Language)
                ? languageItems[value.Language] : languageItems[string.Empty]);

            var theme = new ComboBox { Header = UiText.Get("Settings.Theme"), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(theme, nameof(WinUiSetting.Theme));
            AutomationProperties.SetName(theme, UiText.Get("Settings.Theme"));
            theme.Items.Add(UiText.Get("Settings.Light"));
            theme.Items.Add(UiText.Get("Settings.Dark"));
            theme.Items.Add(UiText.Get("Settings.FollowWindows"));
            theme.SelectionChanged += async (_, _) =>
            {
                if (theme.SelectedIndex >= 0)
                    await ChangeAsync(new(WinUiSetting.Theme, Number: theme.SelectedIndex));
            };
            appearance.Children.Add(theme);
            editors.Add(theme);
            render.Add(value => theme.SelectedIndex = Math.Clamp(value.Theme, 0, 2));
            AddNumber(appearance, UiText.Get("Settings.ToolbarScale"), WinUiSetting.ToolbarScale, s => s.ToolbarScale);
            AddNumber(appearance, UiText.Get("Settings.BoardScale"), WinUiSetting.BlackboardScale, s => s.BlackboardScale);
            AddToggle(appearance, UiText.Get("Settings.ToolbarLabels"), WinUiSetting.ToolbarLabels, s => s.ToolbarLabels);

            var ink = AddSection(UiText.Get("Settings.WhiteboardAndInk"));
            AddToggle(ink, UiText.Get("Settings.WhiteboardBackground"), WinUiSetting.Whiteboard, s => s.Whiteboard);
            AddNumber(ink, UiText.Get("Settings.PenWidth"), WinUiSetting.InkWidth, s => s.InkWidth);
            AddToggle(ink, UiText.Get("Settings.PenCursor"), WinUiSetting.InkCursor, s => s.InkCursor);
            AddToggle(ink, UiText.Get("Settings.RecognizeShapes"), WinUiSetting.InkRecognition, s => s.InkRecognition);
            AddToggle(ink, UiText.Get("Settings.SwitchGestures"), WinUiSetting.AutoSwitchGesture, s => s.AutoSwitchGesture);
            multiTouch = AddToggle(ink, UiText.Get("Settings.MultiTouch"), WinUiSetting.MultiTouch, s => s.MultiTouch);
            AddToggle(ink, UiText.Get("Settings.TwoFingerRotation"), WinUiSetting.RotateWithTwoFingers, s => s.RotateWithTwoFingers);

            var touch = new StackPanel { Spacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            AddToggle(touch, UiText.Get("Settings.NibMode"), WinUiSetting.NibMode, s => s.NibMode);
            AddNumber(touch, UiText.Get("Settings.NibTouchWidth"), WinUiSetting.NibTouchWidth, s => s.NibTouchWidth);
            AddNumber(touch, UiText.Get("Settings.FingerTouchWidth"), WinUiSetting.FingerTouchWidth, s => s.FingerTouchWidth);
            AddNumber(touch, UiText.Get("Settings.NibEraserThreshold"), WinUiSetting.NibEraserThreshold, s => s.NibEraserThreshold);
            AddNumber(touch, UiText.Get("Settings.FingerEraserThreshold"), WinUiSetting.FingerEraserThreshold, s => s.FingerEraserThreshold);
            AddNumber(touch, UiText.Get("Settings.NibEraserScale"), WinUiSetting.NibEraserScale, s => s.NibEraserScale);
            AddNumber(touch, UiText.Get("Settings.FingerEraserScale"), WinUiSetting.FingerEraserScale, s => s.FingerEraserScale);
            AddToggle(touch, UiText.Get("Settings.SpecialTouchScreen"), WinUiSetting.SpecialTouchScreen, s => s.SpecialTouchScreen);
            AddNumber(touch, UiText.Get("Settings.TouchMultiplier"), WinUiSetting.TouchMultiplier, s => s.TouchMultiplier);
            AddToggle(touch, UiText.Get("Settings.QuadInfrared"), WinUiSetting.QuadInfrared, s => s.QuadInfrared);
            ink.Children.Add(new Expander
            {
                Header = UiText.Get("Settings.TouchCalibration"),
                Content = touch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });

            var slides = AddSection(UiText.Get("Settings.Slides"));
            AddToggle(slides, UiText.Get("Settings.DetectPowerPoint"), WinUiSetting.PowerPoint, s => s.PowerPoint);
            AddToggle(slides, UiText.Get("Settings.AnnotateOnSlideShow"), WinUiSetting.AnnotateOnSlideShow, s => s.AnnotateOnSlideShow);
            AddToggle(slides, UiText.Get("Settings.SaveSlideInk"), WinUiSetting.SaveSlideInk, s => s.SaveSlideInk);

            var saves = AddSection(UiText.Get("Settings.Saving"));
            var path = new TextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            render.Add(value => path.Text = value.SaveDirectory);
            saves.Children.Add(new TextBlock { Text = UiText.Get("Settings.AutosaveLocation") });
            saves.Children.Add(path);
            var browse = new Button { Content = UiText.Get("Settings.ChooseFolder") };
            browse.Click += async (_, _) => await PickDirectoryAsync();
            saves.Children.Add(browse);
            editors.Add(browse);
            AddToggle(saves, UiText.Get("Settings.CaptureOnClear"), WinUiSetting.CaptureOnClear, s => s.CaptureOnClear);
            AddToggle(saves, UiText.Get("Settings.SaveInkOnCapture"), WinUiSetting.SaveInkOnCapture, s => s.SaveInkOnCapture);
            AddToggle(saves, UiText.Get("Settings.CleanAutosaves"), WinUiSetting.AutoCleanSavedFiles, s => s.AutoCleanSavedFiles);
            retentionDays = AddNumber(saves, UiText.Get("Settings.AutosaveRetention"), WinUiSetting.SavedFilesRetentionDays,
                s => s.SavedFilesRetentionDays);
            if (!App.IsPackaged)
            {
                var associations = new Button { Content = UiText.Get("Settings.FileAssociations") };
                AutomationProperties.SetAutomationId(associations, "FileAssociations");
                associations.Click += async (_, _) => await OpenFileAssociationsAsync();
                saves.Children.Add(associations);
                editors.Add(associations);
            }

            var startup = AddSection(UiText.Get("Settings.Startup"));
            startupToggle = AddToggle(startup, UiText.Get("Settings.RunAtStartup"), WinUiSetting.RunAtStartup,
                s => s.Startup is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy);
            startupHint = new TextBlock { TextWrapping = TextWrapping.Wrap };
            startup.Children.Add(startupHint);
            render.Add(value =>
            {
                startupHint.Text = value.Startup switch
                {
                    StartupTaskState.DisabledByUser => UiText.Get("Settings.StartupDisabledByUser"),
                    StartupTaskState.DisabledByPolicy => UiText.Get("Settings.StartupPolicyDisabled"),
                    StartupTaskState.EnabledByPolicy => UiText.Get("Settings.StartupPolicyEnabled"),
                    null => UiText.Get("Settings.StartupUnreadable"),
                    _ => string.Empty,
                };
                startupHint.Visibility = startupHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            });
            var startupSettings = new Button { Content = UiText.Get("Settings.WindowsStartupSettings") };
            AutomationProperties.SetAutomationId(startupSettings, "WindowsStartupSettings");
            startupSettings.Click += async (_, _) =>
            {
                refreshStartupOnActivation = true;
                if (!await OpenWindowsSettingsAsync(MainWindow.WindowsStartupSettings)) refreshStartupOnActivation = false;
            };
            startup.Children.Add(startupSettings);
            AddToggle(startup, UiText.Get("Settings.FoldAtStartup"), WinUiSetting.FoldAtStartup, s => s.FoldAtStartup);

            var advanced = new StackPanel { Spacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            AddToggle(advanced, UiText.Get("Settings.EnableWps"), WinUiSetting.Wps, s => s.Wps);
            AddToggle(advanced, UiText.Get("Settings.CaptureSlide"), WinUiSetting.CaptureSlide, s => s.CaptureSlide);
            AddToggle(advanced, UiText.Get("Settings.FoldInSlideShow"), WinUiSetting.FoldInSlideShow, s => s.FoldInSlideShow);
            AddToggle(advanced, UiText.Get("Settings.DateFolders"), WinUiSetting.DateFolders, s => s.DateFolders);
            AddToggle(advanced, UiText.Get("Settings.DisableFullscreenEdgeGestures"), WinUiSetting.DisableFullscreenEdgeGestures,
                s => s.DisableFullscreenEdgeGestures);
            settings.Children.Add(new Expander
            {
                Header = UiText.Get("Settings.MoreOptions"),
                Content = advanced,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });

            var shortcuts = new StackPanel { Spacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            shortcuts.Children.Add(new TextBlock
            {
                Text = UiText.Get("Settings.HotkeyHint"),
                TextWrapping = TextWrapping.Wrap,
            });
            settings.Children.Add(new Expander
            {
                Header = UiText.Get("Settings.GlobalHotkeys"),
                Content = shortcuts,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });
            render.Add(value => RenderHotkeys(shortcuts, value.GlobalHotkeys));

            settings.Children.Add(StoreSettings.CreateSection(Window, pipeName, lifetime.Token));

            var help = AddSection(UiText.Get("Settings.DiagnosticsAndPrivacy"));
            AddToggle(help, UiText.Get("Settings.SendErrors"), WinUiSetting.ErrorReporting, s => s.ErrorReporting);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var diagnostics = new Button { Content = UiText.Get("Settings.Diagnostics") };
            diagnostics.Click += (_, _) => OpenSupportWindow("--diagnostics");
            var guide = new Button { Content = UiText.Get("Settings.ShortcutGuide") };
            guide.Click += (_, _) => OpenSupportWindow("--guide");
            actions.Children.Add(diagnostics);
            actions.Children.Add(guide);
            help.Children.Add(actions);

            var session = AddSection(UiText.Get("Settings.App"));
            var sessionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            AddSessionAction(sessionActions, UiText.Get("Settings.RestartApp"), WinUiSetting.Restart);
            AddSessionAction(sessionActions, UiText.Get("Settings.ExitApp"), WinUiSetting.Exit);
            session.Children.Add(sessionActions);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            notice.Margin = new Thickness(24, 12, 24, 0);
            root.Children.Add(notice);
            var scroll = new ScrollViewer { Content = settings };
            new WinUiAccessibility(Window).AddEntrance(scroll);
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            Window.Content = root;

            var appWindow = Window.AppWindow;
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = true;
            WinUiClassroomTools.Resize(Window, 640, 790, center: true);

            Window.Activated += async (_, args) =>
            {
                if (!refreshStartupOnActivation || busy || closed || args.WindowActivationState == WindowActivationState.Deactivated) return;
                refreshStartupOnActivation = false;
                await ChangeAsync(new(WinUiSetting.Read));
            };
            Window.Closed += (_, _) =>
            {
                closed = true;
                lifetime.Cancel();
                lifetime.Dispose();
            };
            SetBusy(true);
            root.Loaded += LoadSettings;
        }

        private async void LoadSettings(object sender, RoutedEventArgs args)
        {
            ((FrameworkElement)sender).Loaded -= LoadSettings;
            SetBusy(false);
            await ChangeAsync(new(WinUiSetting.Read));
        }

        private StackPanel AddSection(string title)
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            settings.Children.Add(panel);
            return panel;
        }

        private void RenderHotkeys(StackPanel panel, GlobalHotkeySetting[] bindings)
        {
            foreach (var binding in bindings)
            {
                if (!hotkeyRows.TryGetValue(binding.Id, out var row))
                {
                    var input = new TextBox { Width = 160, MaxLength = 60, PlaceholderText = binding.DefaultGesture };
                    AutomationProperties.SetName(input, UiText.Format("Settings.HotkeyInputName", binding.Action));
                    AutomationProperties.SetAutomationId(input, $"GlobalHotkey-{binding.Id}");
                    var apply = new Button { Content = UiText.Get("Settings.Apply") };
                    AutomationProperties.SetName(apply, UiText.Format("Settings.HotkeyApplyName", binding.Action));
                    apply.Click += async (_, _) => await ChangeAsync(new(WinUiSetting.GlobalHotkey, Text: input.Text, Id: binding.Id));
                    var reset = new Button { Content = UiText.Get("Settings.ResetDefault") };
                    AutomationProperties.SetName(reset, UiText.Format("Settings.HotkeyResetName", binding.Action));
                    reset.Click += async (_, _) => await ChangeAsync(new(WinUiSetting.GlobalHotkey, Id: binding.Id));
                    var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    keys.Children.Add(input);
                    keys.Children.Add(apply);
                    keys.Children.Add(reset);
                    var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
                    AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
                    panel.Children.Add(new TextBlock { Text = binding.Action });
                    panel.Children.Add(keys);
                    panel.Children.Add(status);
                    editors.Add(input);
                    editors.Add(apply);
                    editors.Add(reset);
                    row = (input, status, null);
                }
                if (row.Gesture != binding.Gesture) row.Input.Text = binding.Gesture;
                row.Status.Text = binding.Error ?? (binding.Gesture.Length == 0 ? UiText.Get("Settings.HotkeyDisabled") : string.Empty);
                row.Status.Visibility = row.Status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                hotkeyRows[binding.Id] = (row.Input, row.Status, binding.Gesture);
            }
        }

        private ToggleSwitch AddToggle(StackPanel panel, string label, WinUiSetting setting, Func<WinUiSettingsState, bool> read)
        {
            var toggle = new ToggleSwitch { Header = label, OnContent = UiText.Get("Settings.On"), OffContent = UiText.Get("Settings.Off") };
            AutomationProperties.SetAutomationId(toggle, setting.ToString());
            AutomationProperties.SetName(toggle, label);
            toggle.Toggled += async (_, _) => await ChangeAsync(new(setting, Enabled: toggle.IsOn));
            panel.Children.Add(toggle);
            editors.Add(toggle);
            render.Add(value => toggle.IsOn = read(value));
            return toggle;
        }

        private NumberBox AddNumber(StackPanel panel, string label, WinUiSetting setting, Func<WinUiSettingsState, double> read)
        {
            var preference = NumericPreference.All[setting];
            var number = new NumberBox
            {
                Header = label,
                Minimum = preference.Minimum,
                Maximum = preference.Maximum,
                SmallChange = preference.Step,
                LargeChange = preference.Step * 4,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            };
            AutomationProperties.SetAutomationId(number, setting.ToString());
            AutomationProperties.SetName(number, label);
            number.ValueChanged += async (_, change) =>
            {
                if (double.IsFinite(change.NewValue))
                    await ChangeAsync(new(setting, Number: change.NewValue));
            };
            panel.Children.Add(number);
            editors.Add(number);
            render.Add(value => number.Value = Math.Clamp(read(value), preference.Minimum, preference.Maximum));
            return number;
        }

        private void AddSessionAction(StackPanel panel, string label, WinUiSetting setting)
        {
            var button = new Button { Content = label };
            AutomationProperties.SetAutomationId(button, setting.ToString());
            button.Click += async (_, _) =>
            {
                var confirmation = new ContentDialog
                {
                    XamlRoot = settings.XamlRoot,
                    Title = label,
                    Content = UiText.Get("Settings.SaveBeforeExit"),
                    PrimaryButtonText = setting == WinUiSetting.Restart ? UiText.Get("Settings.Restart") : UiText.Get("Settings.Exit"),
                    CloseButtonText = UiText.Get("Settings.Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                };
                WinUiClassroomTools.SetLanguage(confirmation);
                if (await confirmation.ShowAsync() == ContentDialogResult.Primary)
                    await ChangeAsync(new(setting, Enabled: true));
            };
            panel.Children.Add(button);
            editors.Add(button);
        }

        private async Task OpenFileAssociationsAsync()
        {
            if (busy || closed) return;
            SetBusy(true);
            try
            {
                bool replaceOwner = !InstallHelper.IsAssociationOwner();
                if (replaceOwner)
                {
                    var confirmation = new ContentDialog
                    {
                        XamlRoot = settings.XamlRoot,
                        Title = UiText.Get("Settings.FileAssociations"),
                        Content = new TextBlock
                        {
                            Text = UiText.Format("Settings.FileAssociationsConfirm", Environment.ProcessPath),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        PrimaryButtonText = UiText.Get("Settings.Apply"),
                        CloseButtonText = UiText.Get("Settings.Cancel"),
                        DefaultButton = ContentDialogButton.Close,
                    };
                    WinUiClassroomTools.SetLanguage(confirmation);
                    if (await confirmation.ShowAsync() != ContentDialogResult.Primary || closed) return;
                }

                InstallHelper.RegisterUnpackagedApp(replaceOwner);
                await OpenWindowsSettingsAsync(InstallHelper.DefaultAppsSettings, "ms-settings:defaultapps");
            }
            catch (Exception failure)
            {
                LogHelper.WriteLogToFile($"Could not register file associations: {failure}", LogHelper.LogType.Error);
                if (!closed) ShowError(UiText.Get("Settings.ChangeFailed"));
            }
            finally { if (!closed) SetBusy(false); }
        }

        private async Task<bool> OpenWindowsSettingsAsync(string page, string fallback = null)
        {
            try
            {
                if (await global::Windows.System.Launcher.LaunchUriAsync(new Uri(page))) return true;
                if (fallback != null && await global::Windows.System.Launcher.LaunchUriAsync(new Uri(fallback))) return true;
            }
            catch (Exception failure)
            {
                LogHelper.WriteLogToFile($"Could not open Windows Settings: {failure}", LogHelper.LogType.Error);
            }
            if (!closed) ShowError(UiText.Get("Actions.OpenSettingsFailed"));
            return false;
        }

        private async Task PickDirectoryAsync()
        {
            if (busy || closed || state == null) return;
            SetBusy(true);
            try
            {
                string directory = await WinUiPickers.PickSaveDirectoryAsync(
                    WinRT.Interop.WindowNative.GetWindowHandle(Window), state.SaveDirectory);
                if (!closed && !string.IsNullOrWhiteSpace(directory))
                    await ApplyAsync(new(WinUiSetting.SaveDirectory, Text: directory));
            }
            catch (Exception failure)
            {
                if (!closed) ShowError(UiText.Format("Settings.ChooseFolderFailed", failure.Message));
            }
            finally { if (!closed) SetBusy(false); }
        }

        private async Task ChangeAsync(WinUiSettingsRequest request)
        {
            if (busy || closed) return;
            SetBusy(true);
            try { await ApplyAsync(request); }
            finally { if (!closed) SetBusy(false); }
        }

        private async Task ApplyAsync(WinUiSettingsRequest request)
        {
            try
            {
                var response = await SettingsPipe.RequestAsync(pipeName, request, lifetime.Token);
                if (closed) return;
                var previous = state;
                state = request.Setting is not (WinUiSetting.Read or WinUiSetting.RunAtStartup) && previous != null
                    ? response.State with { Startup = previous.Startup }
                    : response.State;
                ((FrameworkElement)Window.Content).RequestedTheme = state.Theme switch
                {
                    0 => ElementTheme.Light,
                    1 => ElementTheme.Dark,
                    _ => ElementTheme.Default,
                };
                foreach (var update in render) update(state);
                if (request.Setting == WinUiSetting.GlobalHotkey && response.Error == null &&
                    hotkeyRows.TryGetValue(request.Id, out var hotkey))
                    hotkey.Input.Text = hotkey.Gesture;
                if (string.IsNullOrEmpty(response.Error)) notice.IsOpen = false;
                else ShowError(response.Error);
            }
            catch (Exception)
            {
                if (closed) return;
                if (state != null)
                    foreach (var update in render) update(state);
                ShowError(UiText.Get("Settings.ConnectionFailed"));
            }
        }

        private void OpenSupportWindow(string action)
        {
            try { WinUiWindowLauncher.Open(action); }
            catch (Exception) { ShowError(UiText.Get("Settings.ToolWindowFailed")); }
        }

        private void SetBusy(bool working)
        {
            busy = working;
            bool enabled = !working && state != null;
            foreach (var editor in editors) editor.IsEnabled = enabled;
            multiTouch.IsEnabled = enabled && !state.AutoSwitchGesture;
            retentionDays.IsEnabled = enabled && state.AutoCleanSavedFiles;
            startupToggle.IsEnabled = enabled && state.Startup is StartupTaskState.Disabled or StartupTaskState.Enabled;
            if (notice.ActionButton is Button retry) retry.IsEnabled = !working;
        }

        private void ShowError(string message)
        {
            notice.Message = message;
            notice.Severity = InfoBarSeverity.Error;
            notice.IsOpen = true;
        }
    }
}
