using FluentCanvas.Localization;
using System.Windows;
using System.Windows.Input;
using FluentCanvas.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FluentCanvas
{
    internal sealed record GlobalHotkeySetting(string Id, string Action, string Gesture, string DefaultGesture, string Error);

    public partial class MainWindow : Window
    {
        private sealed class GlobalHotkey(string name, string label, string defaultGesture, Action action)
        {
            internal string Name { get; } = name;
            internal string Label { get; } = label;
            internal string DefaultGesture { get; } = defaultGesture;
            internal Action Action { get; } = action;
            internal int Registration { get; set; } = -1;
            internal string Error { get; set; }
        }

        private Hotkey hotkeys;
        private GlobalHotkey[] globalHotkeys = [];
        private static readonly KeyGestureConverter hotkeyConverter = new();

        private void RegisterGlobalHotkeys()
        {
            if (hotkeys != null) return;
            hotkeys = new Hotkey(this);
            globalHotkeys =
            [
                new("EndSlideShow", UiText.Get("Settings.HotkeyEndSlides"), "Shift+Esc", HotKey_ExitPPTSlideShow),
                new("ClearCanvas", UiText.Get("Settings.HotkeyClear"), "Ctrl+E", HotKey_Clear),
                new("Capture", UiText.Get("Settings.HotkeyCapture"), "Alt+C", HotKey_Capture),
                new("ToggleToolbar", UiText.Get("Settings.HotkeyTools"), "Alt+V", HotKey_Hide),
                new("Draw", UiText.Get("Settings.HotkeyDraw"), "Alt+D", HotKey_DrawTool),
                new("Pointer", UiText.Get("Settings.HotkeyPointer"), "Alt+Q", HotKey_QuitDrawMode),
                new("Board", UiText.Get("Settings.HotkeyBoard"), "Alt+B", HotKey_Board),
            ];
            foreach (var binding in globalHotkeys)
                (binding.Registration, binding.Error) = RegisterHotkey(binding, ReadHotkeyGesture(binding));

            Closed += (_, _) => hotkeys.Dispose();
            var unavailable = globalHotkeys.Where(binding => binding.Error != null).Select(ReadHotkeyGesture).ToArray();
            if (unavailable.Length > 0)
            {
                string keys = string.Join(UiText.ActiveCulture.TextInfo.ListSeparator + " ", unavailable);
                LogHelper.WriteLogToFile("Unable to register global hotkeys: " + keys, LogHelper.LogType.Error);
                ShowNotificationAsync(UiText.Format("Settings.HotkeyUnavailable", keys));
            }
        }

        private string ReadHotkeyGesture(GlobalHotkey binding) =>
            Settings.GlobalHotkeys?.TryGetValue(binding.Name, out string gesture) == true ? gesture ?? string.Empty : binding.DefaultGesture;

        internal GlobalHotkeySetting[] ReadGlobalHotkeys() => globalHotkeys.Select(binding =>
            new GlobalHotkeySetting(binding.Name, binding.Label, ReadHotkeyGesture(binding), binding.DefaultGesture, binding.Error)).ToArray();

        private (int Id, string Error) RegisterHotkey(GlobalHotkey binding, string text)
        {
            if (text.Length == 0) return (-1, null);
            if (!TryParseHotkey(text, out var gesture)) return (-1, UiText.Get("Settings.HotkeyInvalid"));
            return hotkeys.Register(gesture, () => { if (IsEnabled) binding.Action(); });
        }

        private static bool TryParseHotkey(string text, out KeyGesture gesture)
        {
            try { gesture = hotkeyConverter.ConvertFromInvariantString(text) as KeyGesture; }
            catch (Exception failure) when (failure is ArgumentException or FormatException or NotSupportedException)
            {
                gesture = null;
            }
            return gesture != null && gesture.Key != Key.None && gesture.Modifiers != ModifierKeys.None;
        }

        private string ApplyGlobalHotkey(string name, string text)
        {
            var binding = globalHotkeys.FirstOrDefault(candidate => candidate.Name == name);
            if (binding == null) return UiText.Get("Settings.HotkeyUnknown");

            string before = ReadHotkeyGesture(binding);
            string after = text?.Trim() ?? binding.DefaultGesture;
            if (after.Length != 0)
            {
                if (!TryParseHotkey(after, out var gesture)) return UiText.Get("Settings.HotkeyModifier");
                after = hotkeyConverter.ConvertToInvariantString(gesture);
            }

            bool changed = !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
            if (!changed && binding.Error == null) return null;
            var (id, error) = RegisterHotkey(binding, after);
            if (error != null)
            {
                if (!changed) binding.Error = error;
                return $"{after} {error}";
            }

            if (changed)
            {
                var previousSettings = Settings.GlobalHotkeys;
                var settings = previousSettings != null ? new Dictionary<string, string>(previousSettings) : new();
                if (after == binding.DefaultGesture) settings.Remove(binding.Name);
                else settings[binding.Name] = after;
                Settings.GlobalHotkeys = settings;
                if (!SaveSettingsToFile())
                {
                    Settings.GlobalHotkeys = previousSettings;
                    hotkeys.Unregister(id);
                    return UiText.Get("Settings.HotkeySaveFailed");
                }
            }

            hotkeys.Unregister(binding.Registration);
            (binding.Registration, binding.Error) = (id, null);
            return null;
        }

        private void HotKey_ExitPPTSlideShow()
        {
            if(BtnPPTSlideShowEnd.Visibility == Visibility.Visible)
            {
                BtnPPTSlideShowEnd_Click(null, null);
            }
        }

        private void HotKey_Clear()
        {
            SymbolIconDelete_MouseUp(null, null);
        }

        private void HotKey_Capture()
        {
            SaveScreenShotToDesktop();
        }
        
        private void HotKey_Hide()
        {
            FloatingToolbarDrag_MouseUp(null, null);
        }

        private void HotKey_DrawTool()
        {
            PenIcon_Click(null, null);
        }

        private void HotKey_QuitDrawMode()
        {
            if (currentMode != 0)
            {
                ImageBlackboard_Click(null, null);
            }
            CursorIcon_Click(null, null);
        }

        private void HotKey_Board()
        {
            ImageBlackboard_Click(null, null);
        }

        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (BtnPPTSlideShowEnd.Visibility != Visibility.Visible || currentMode != 0) return;
            if (e.Delta >= 120)
            {
                BtnPPTSlidesUp_Click(null, null);
            }
            else if (e.Delta <= -120)
            {
                BtnPPTSlidesDown_Click(null, null);
            }
        }

        private void Main_Grid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.Escape && (StopNewShape() || DismissToolbarFlyout())) || HandlePageNavigationKey(e))
            {
                e.Handled = true;
                return;
            }
            if (BtnPPTSlideShowEnd.Visibility != Visibility.Visible || currentMode != 0) return;
            if (e.Key == Key.Space && Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase) return;

            if (e.Key == Key.Down || e.Key == Key.Right || e.Key == Key.N || e.Key == Key.Space)
            {
                BtnPPTSlidesDown_Click(null, null);
            }
            if (e.Key == Key.Up || e.Key == Key.Left || e.Key == Key.P)
            {
                BtnPPTSlidesUp_Click(null, null);
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                KeyExit(null, null);
            }
        }

        private void CommandBinding_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        private void Undo_CanExecute(object sender, CanExecuteRoutedEventArgs e) =>
            e.CanExecute = HasNewShapeInProgress || timeMachine.CanUndo;

        private void Redo_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = timeMachine.CanRedo;

        private void ToolbarUsePen(object sender, ExecutedRoutedEventArgs e) => PenIcon_Click(sender, e);

        private void ToolbarUseEraser(object sender, ExecutedRoutedEventArgs e)
        {
            if (currentMode == 0)
                EraserIcon_Click(sender, e);
            else
                BoardEraserIcon_Click(sender, e);
        }

        private void ToolbarEraseStrokes(object sender, ExecutedRoutedEventArgs e)
        {
            if (currentMode == 0)
                EraserIconByStrokes_Click(sender, e);
            else
                BoardEraserIconByStrokes_Click(sender, e);
        }

        private void ToolbarUnfold(object sender, ExecutedRoutedEventArgs e) => UnFoldFloatingBar_MouseUp(e.OriginalSource, null);

        private void HotKey_Undo(object sender, ExecutedRoutedEventArgs e)
        {
            try
            {
                SymbolIconUndo_Click(null, null);
            }
            catch { }
        }

        private void HotKey_Redo(object sender, ExecutedRoutedEventArgs e)
        {
            try
            {
                SymbolIconRedo_Click(null, null);
            }
            catch { }
        }

        private void KeyExit(object sender, ExecutedRoutedEventArgs e)
        {
            BtnPPTSlideShowEnd_Click(null, null);
        }

        private void KeyChangeToSelect(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is "Toolbar" || currentMode != 0 ||
                StackPanelCanvasControls.Visibility == Visibility.Visible)
            {
                SymbolIconSelect_Click(null, null);
            }
        }

        private void KeyChangeToEraser(object sender, ExecutedRoutedEventArgs e)
        {
            if (StackPanelCanvasControls.Visibility == Visibility.Visible)
            {
                if (activeToolbarTool == "eraser")
                {
                    EraserIconByStrokes_Click(null, null);
                }
                else
                {
                    EraserIcon_Click(null, null);
                }
            }
        }

        private void KeyDrawLine(object sender, ExecutedRoutedEventArgs e)
        {
            if (currentMode != 0 || StackPanelCanvasControls.Visibility == Visibility.Visible)
            {
                SelectShapeTool(ShapeTool.Line, false);
            }
        }
    }
}
