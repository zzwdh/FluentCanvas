using FluentCanvas.Helpers;
using FluentCanvas.Services;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using Windows.ApplicationModel.Activation;

namespace FluentCanvas
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private readonly Queue<string> activationFiles = new();
        private MainWindow activationWindow;
        private bool openingFiles;

        public static string[] StartArgs = Array.Empty<string>();
        public static string RootPath =>
            Helpers.StorageHelper.UserDataFolder + System.IO.Path.DirectorySeparatorChar;

        private static string[] GetActivationFiles(AppActivationArguments args, string[] fallback = null)
        {
            if (args?.Kind == ExtendedActivationKind.File && args.Data is IFileActivatedEventArgs fileArgs)
            {
                return InkActivation.SelectFiles(fileArgs.Files.OfType<global::Windows.Storage.IStorageFile>()
                    .Select(file => file.Path));
            }

            if (fallback != null)
            {
                var files = InkActivation.SelectFiles(fallback);
                if (files.Length > 0) return files;
            }
            if (args?.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launch)
                return InkActivation.FromCommandLine(launch.Arguments);
            return Array.Empty<string>();
        }

        public static bool IsPackaged
        {
            get
            {
                try
                {
                    _ = global::Windows.ApplicationModel.Package.Current.Id;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public App()
        {
            ErrorReporting.Initialize();
            Exit += (_, _) => ErrorReporting.Close();
            this.Startup += new StartupEventHandler(App_Startup);
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            ErrorReporting.Capture(e.Exception);
            FluentCanvas.MainWindow.ShowNewMessage("抱歉，出现未预期的异常，可能导致 FluentCanvas 画板运行不稳定。\n建议保存墨迹后重启应用。", true);
            LogHelper.NewLog(e.Exception.ToString());
            e.Handled = true;
        }

        private async void App_Startup(object sender, StartupEventArgs e)
        {
            if (e.Args.Length == 1 && e.Args[0] == "--verify-sentry")
            {
                Environment.ExitCode = ErrorReporting.VerifyDelivery();
                Shutdown(Environment.ExitCode);
                return;
            }

            StartArgs = e.Args;
            AppActivationArguments activation = null;
            bool redirecting = false;
            try
            {
                var current = AppInstance.GetCurrent();
                // Subscribe before publishing the key: a second launch can arrive
                // while the first window is still being constructed.
                current.Activated += AppInstance_Activated;
                activation = current.GetActivatedEventArgs();
                var primary = AppInstance.FindOrRegisterForKey("FluentCanvas");
                if (!primary.IsCurrent && !Array.Exists(e.Args, arg => arg == "-m"))
                {
                    redirecting = true;
                    await primary.RedirectActivationToAsync(activation);
                    Shutdown();
                    return;
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"Windows App SDK activation failed: {ex}", LogHelper.LogType.Error);
                if (redirecting)
                {
                    System.Windows.MessageBox.Show("无法将文件发送到已打开的 FluentCanvas。请使用画板中的打开按钮重试。", "FluentCanvas");
                    Shutdown(1);
                    return;
                }
            }

            foreach (var path in GetActivationFiles(activation, e.Args))
                activationFiles.Enqueue(path);

            InstallHelper.RunStartupTasks();
            LogHelper.NewLog($"FluentCanvas Starting (Version: {Assembly.GetExecutingAssembly().GetName().Version})");
            ServiceRegistration.RegisterServices();
            Locator.Initialize();

            MainWindow = new MainWindow();
            MainWindow.Show();
        }

        private void AppInstance_Activated(object sender, AppActivationArguments args)
        {
            var files = GetActivationFiles(args);
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var file in files)
                    activationFiles.Enqueue(file);
                if (activationWindow == null)
                    return;

                if (activationWindow.WindowState == WindowState.Minimized)
                    activationWindow.WindowState = WindowState.Normal;
                activationWindow.Show();
                activationWindow.Activate();
                OpenPendingFiles();
            });
        }

        internal void EnableFileActivation(MainWindow window)
        {
            activationWindow = window;
            OpenPendingFiles();
        }

        private void OpenPendingFiles()
        {
            if (openingFiles) return;
            openingFiles = true;
            try
            {
                while (activationFiles.TryDequeue(out var file))
                    activationWindow.LoadInkCanvasFile(file);
            }
            finally
            {
                openingFiles = false;
            }
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            try
            {
                if (System.Windows.Forms.SystemInformation.MouseWheelScrollLines == -1)
                    e.Handled = false;
                else
                    try
                    {
                        ScrollViewerEx SenderScrollViewer = (ScrollViewerEx)sender;
                        SenderScrollViewer.ScrollToVerticalOffset(SenderScrollViewer.VerticalOffset - e.Delta * 10 * System.Windows.Forms.SystemInformation.MouseWheelScrollLines / (double)120);
                        e.Handled = true;
                    }
                    catch {  }
            }
            catch {  }
        }
    }
}
