using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using FluentCanvas.Services;
using FluentCanvas.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using File = System.IO.File;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        #region Window Initialization

        private readonly SettingsWindowHost settingsWindow = new();
        private readonly ISettingsService settingsService;
        private readonly NotificationsViewModel notifications = new();
        private readonly PptNavigationViewModel navigation = new();

        internal MainWindow(ISettingsService settingsService, IPowerPointService powerPoint,
            PresentationSlideShowService slideShows)
        {
            this.settingsService = settingsService;
            this.pptService = powerPoint;
            this.slideShows = slideShows;
            InitializeComponent();
            NotificationPanel.DataContext = notifications;
            PresentationNavigation.DataContext = navigation;

            BlackboardLeftSide.Visibility = Visibility.Collapsed;
            BlackboardCenterSide.Visibility = Visibility.Collapsed;
            BlackboardRightSide.Visibility = Visibility.Collapsed;

            BorderTools.Visibility = Visibility.Collapsed;

            BtnPPTSlideShowEnd.Visibility = Visibility.Collapsed;


            TwoFingerGestureBorder.Visibility = Visibility.Collapsed;
            BoardTwoFingerGestureBorder.Visibility = Visibility.Collapsed;
            BorderDrawShape.Visibility = Visibility.Collapsed;
            BoardBorderDrawShape.Visibility = Visibility.Collapsed;

            GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;

            ViewboxFloatingBar.Margin = new Thickness((SystemParameters.WorkArea.Width - 284) / 2, SystemParameters.WorkArea.Height - 60, -2000, -200);
            ViewboxFloatingBarMarginAnimation();

            InitTimers();
            InitPowerPoint();
            timeMachine.OnRedoStateChanged += TimeMachine_OnRedoStateChanged;
            timeMachine.OnUndoStateChanged += TimeMachine_OnUndoStateChanged;
            inkCanvas.Strokes.StrokesChanged += StrokesOnStrokesChanged;

            Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
            systemVisualSettings.AdvancedEffectsEnabledChanged += SystemVisualSettings_AdvancedEffectsEnabledChanged;
            try
            {
                if (File.Exists(Path.Combine(AppContext.BaseDirectory, "SpecialVersion.ini"))) _ = ApplySpecialVersionSettingsAsync();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
            }

            CheckColorTheme(true);
        }

        #endregion

        #region FluentCanvas Functions

        DrawingAttributes drawingAttributes;
        private void loadPenCanvas()
        {
            try
            {
                drawingAttributes = inkCanvas.DefaultDrawingAttributes;
                drawingAttributes.Color = Colors.Red;

                drawingAttributes.Height = 2.5;
                drawingAttributes.Width = 2.5;

                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                inkCanvas.Gesture += InkCanvas_Gesture;
            }
            catch { }
        }

        private void InkCanvas_Gesture(object sender, InkCanvasGestureEventArgs e)
        {
            ReadOnlyCollection<GestureRecognitionResult> gestures = e.GetGestureRecognitionResults();
            try
            {
                foreach (GestureRecognitionResult gest in gestures)
                {
                    if (BtnPPTSlideShowEnd.Visibility == Visibility.Visible)
                    {
                        if (gest.ApplicationGesture == ApplicationGesture.Left)
                        {
                            BtnPPTSlidesDown_Click(null, null);
                        }
                        if (gest.ApplicationGesture == ApplicationGesture.Right)
                        {
                            BtnPPTSlidesUp_Click(null, null);
                        }
                    }
                }
            }
            catch { }
        }

        private void inkCanvas_EditingModeChanged(object sender, RoutedEventArgs e)
        {
            var inkCanvas1 = sender as InkCanvas;
            if (inkCanvas1 == null) return;
            if (Settings.Canvas.IsShowCursor)
            {
                if (inkCanvas1.EditingMode == InkCanvasEditingMode.Ink || drawingShapeMode != ShapeTool.Pen)
                {
                    inkCanvas1.ForceCursor = true;
                }
                else
                {
                    inkCanvas1.ForceCursor = false;
                }
            }
            else
            {
                inkCanvas1.ForceCursor = false;
            }
            if (inkCanvas1.EditingMode == InkCanvasEditingMode.Ink) forcePointEraser = !forcePointEraser;
        }

        #endregion FluentCanvas Functions

        #region Definations and Loading

        private Settings Settings
        {
            get => settingsService.Model;
            set => settingsService.Model = value;
        }
        bool isLoaded = false;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            loadPenCanvas();
            //加载设置
            LoadSettings(true);
            SystemEvents_UserPreferenceChanged(null, null);

            LogHelper.WriteLogToFile("FluentCanvas Loaded", LogHelper.LogType.Event);
            isLoaded = true;
            RegisterGlobalHotkeys();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (sessionCloseApproved)
            {
                LogHelper.WriteLogToFile("FluentCanvas closing", LogHelper.LogType.Event);
                return;
            }
            e.Cancel = true;
            // WPF must finish raising Closing before a replacement Close can run.
            if (!sessionEndPending)
                _ = Dispatcher.BeginInvoke(new Action(() => _ = FinishSessionAsync(restart: false,
                    confirmWindowClose: Settings.Advanced.IsSecondConfimeWhenShutdownApp)));
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
            systemVisualSettings.AdvancedEffectsEnabledChanged -= SystemVisualSettings_AdvancedEffectsEnabledChanged;
            LogHelper.WriteLogToFile("FluentCanvas closed", LogHelper.LogType.Event);
        }

        #endregion Definations and Loading
    }
}
