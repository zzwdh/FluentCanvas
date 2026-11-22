using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using FluentCanvas.Helpers;
using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Windows.UI.ViewManagement;
using Application = System.Windows.Application;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        private ResourceDictionary contrastResources;
        private ResourceDictionary opaqueChromeResources;
        private readonly UISettings systemVisualSettings = new();

        public string GetMainWindowTheme()
        {
            if (currentMode != 0)
                return Settings.Canvas.UsingWhiteboard ? "Light" : "Dark";

            return IsLightApplicationTheme() ? "Light" : "Dark";
        }

        private bool IsLightApplicationTheme() => Settings.Appearance.Theme switch
        {
            0 => true,
            1 => false,
            _ => IsSystemThemeLight()
        };

        private static bool SetThemeDictionary(string activePath, string inactivePath)
        {
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            int slot = -1;
            bool changed = false;
            for (int index = 0; index < dictionaries.Count; index++)
            {
                string source = dictionaries[index].Source?.OriginalString;
                if (source == null ||
                    (!source.EndsWith(activePath, StringComparison.OrdinalIgnoreCase) &&
                     !source.EndsWith(inactivePath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (slot < 0)
                {
                    slot = index;
                    if (!source.EndsWith(activePath, StringComparison.OrdinalIgnoreCase))
                    {
                        dictionaries[index] = new ResourceDictionary { Source = new Uri(activePath, UriKind.Relative) };
                        changed = true;
                    }
                }
                else
                {
                    dictionaries.RemoveAt(index);
                    changed = true;
                    index--;
                }
            }

            if (slot >= 0) return changed;

            dictionaries.Add(new ResourceDictionary { Source = new Uri(activePath, UriKind.Relative) });
            return true;
        }

        private void ApplyCanvasTheme()
        {
            const string light = "Resources/Styles/Light.xaml";
            const string dark = "Resources/Styles/Dark.xaml";
            const string lightBoard = "Resources/Styles/Light-Board.xaml";
            const string darkBoard = "Resources/Styles/Dark-Board.xaml";

            bool useLightTheme = IsLightApplicationTheme();
            bool useWhiteboard = Settings.Canvas.UsingWhiteboard;
            bool themeChanged = SetThemeDictionary(useLightTheme ? light : dark, useLightTheme ? dark : light);
            themeChanged |= SetThemeDictionary(useWhiteboard ? lightBoard : darkBoard, useWhiteboard ? darkBoard : lightBoard);

            bool highContrast = SystemParameters.HighContrast;
            var elementTheme = highContrast ? ElementTheme.Default : (useLightTheme ? ElementTheme.Light : ElementTheme.Dark);
            ApplicationTheme? applicationTheme = highContrast ? null : (useLightTheme ? ApplicationTheme.Light : ApplicationTheme.Dark);
            if (ThemeManager.GetRequestedTheme(window) != elementTheme)
                ThemeManager.SetRequestedTheme(window, elementTheme);
            if (ThemeManager.Current.ApplicationTheme != applicationTheme)
                ThemeManager.Current.ApplicationTheme = applicationTheme;

            ApplyContrastResources(highContrast);
            ApplyChromeTransparency(!highContrast && !systemVisualSettings.AdvancedEffectsEnabled, themeChanged);
            ApplyFloatingToolbarBackground(highContrast);
        }

        private void ApplyChromeTransparency(bool useOpaqueChrome, bool themeChanged)
        {
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            if (opaqueChromeResources != null && (!useOpaqueChrome || themeChanged))
            {
                dictionaries.Remove(opaqueChromeResources);
                opaqueChromeResources = null;
            }
            if (!useOpaqueChrome || opaqueChromeResources != null) return;

            opaqueChromeResources = new ResourceDictionary
            {
                ["OverlayChromeOpacity"] = 1d,
                ["PresentationToolbarOpacity"] = 1d,
                ["NotificationSurfaceOpacity"] = 1d,
                ["ConfirmationBackdrop"] = BackdropType.None
            };
            foreach (string key in new[]
                     {
                         "FloatBarBackground", "FloatBarBackgroundWithoutOpacity", "FloatBarTitleBackground", "FloatBarBorderBrush",
                         "BoardBarBackground", "BoardBarTitleBackground", "BoardBarBorderBrush",
                         "FloatingToolbarSurfaceBrush", "FloatingToolbarColorfulSurfaceBrush"
                     })
            {
                var brush = ((Brush)Application.Current.FindResource(key)).CloneCurrentValue();
                brush.Opacity = 1;
                if (brush is SolidColorBrush solid)
                    solid.Color = Color.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B);
                else if (brush is GradientBrush gradient)
                    foreach (GradientStop stop in gradient.GradientStops)
                        stop.Color = Color.FromRgb(stop.Color.R, stop.Color.G, stop.Color.B);

                brush.Freeze();
                opaqueChromeResources.Add(key, brush);
            }
            dictionaries.Add(opaqueChromeResources);
        }

        private void ApplyContrastResources(bool highContrast)
        {
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            if (highContrast && contrastResources == null)
            {
                contrastResources = new ResourceDictionary
                {
                    Source = new Uri("Resources/Styles/HighContrast-Toolbar.xaml", UriKind.Relative)
                };
                dictionaries.Add(contrastResources);
            }
            else if (!highContrast && contrastResources != null)
            {
                dictionaries.Remove(contrastResources);
                contrastResources = null;
            }
        }

        private void ApplyFloatingToolbarBackground(bool highContrast)
        {
            string resourceKey = Settings.Appearance.IsColorfulViewboxFloatingBar && !highContrast
                ? "FloatingToolbarColorfulSurfaceBrush"
                : "FloatingToolbarSurfaceBrush";
            foreach (Border surface in new[]
                     {
                         EnableTwoFingerGestureBorder, BorderFloatingBarMainControls,
                         BorderFloatingBarMoveControls, BtnPPTSlideShowEnd
                     })
            {
                surface.SetResourceReference(Border.BackgroundProperty, resourceKey);
            }
        }

        private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(ApplyCanvasTheme));
                return;
            }

            ApplyCanvasTheme();
        }

        private void SystemVisualSettings_AdvancedEffectsEnabledChanged(UISettings sender, object args) =>
            SystemEvents_UserPreferenceChanged(null, null);

        private void SystemParameters_StaticPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) ViewboxFloatingBarMarginAnimation();
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
                SystemEvents_UserPreferenceChanged(null, null);

            if (e.PropertyName is not (nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))) return;
            if (Dispatcher.CheckAccess()) RespectToolbarMotionPreference();
            else if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(RespectToolbarMotionPreference);
        }

        private void RespectToolbarMotionPreference()
        {
            if (AnimationsHelper.CanAnimate) return;
            if (floatingToolbarMarginClock != null) CompleteFloatingToolbarMotion(hideToolbar: true);
            if (sidePanelMarginClock != null) CompleteFoldHandleMotion(isFloatingBarFolded);
        }

        private static bool IsSystemThemeLight()
        {
            try
            {
                using var themeKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                object value = themeKey?.GetValue("AppsUseLightTheme") ?? themeKey?.GetValue("SystemUsesLightTheme");
                return value is int theme && theme == 1;
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.Security.SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
