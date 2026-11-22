#nullable enable

using FluentCanvas.Localization;
using FluentCanvas.Helpers;
using Microsoft.Win32.SafeHandles;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FluentCanvas.Services
{
    internal sealed class SettingsWindowHost
    {
        private Process? settingsProcess;

        internal async Task OpenAsync(MainWindow owner)
        {
            if (settingsProcess != null)
            {
                try
                {
                    settingsProcess.Refresh();
                    nint handle = settingsProcess.MainWindowHandle;
                    if (handle != 0)
                    {
                        if (IsIconic(handle)) ShowWindow(handle, 9);
                        SetForegroundWindow(handle);
                    }
                }
                catch (InvalidOperationException) { }
                return;
            }

            string pipeName = SettingsPipe.Prefix + Guid.NewGuid().ToString("N");
            using var stop = new CancellationTokenSource();
            Process? process = null;
            void OnOwnerClosed(object? sender, EventArgs args)
            {
                stop.Cancel();
                CloseChildWindow(process);
            }
            owner.Closed += OnOwnerClosed;
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                process = WinUiWindowLauncher.Start("--winui-settings", pipeName);
                settingsProcess = process;

                Task requests = ServeAsync(server, process.Id, owner, stop.Token);
                Task exited = process.WaitForExitAsync(stop.Token);
                await Task.WhenAny(requests, exited);
                stop.Cancel();
                await requests;
                if (process.HasExited && process.ExitCode != 0 && !owner.Dispatcher.HasShutdownStarted)
                {
                    owner.ShowNotificationAsync(UiText.Get("Settings.SettingsExited"));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                LogHelper.WriteLogToFile($"WinUI settings startup failed: {error}", LogHelper.LogType.Error);
                if (!owner.Dispatcher.HasShutdownStarted)
                    owner.ShowNotificationAsync(UiText.Get("Settings.OpenSettingsFailed"));
            }
            finally
            {
                owner.ReleaseStoreUpdate();
                owner.Closed -= OnOwnerClosed;
                settingsProcess = null;
                if (process != null)
                {
                    CloseChildWindow(process);
                    process.Dispose();
                }
            }
        }

        private static void CloseChildWindow(Process? process)
        {
            try { if (process != null && !process.HasExited) process.CloseMainWindow(); }
            catch (InvalidOperationException) { }
        }

        private static async Task ServeAsync(NamedPipeServerStream server, int clientId, MainWindow owner,
            CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                try
                {
                    if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint connectedPid) || connectedPid != (uint)clientId)
                        continue;

                    using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    read.CancelAfter(TimeSpan.FromSeconds(5));
                    var request = await SettingsPipe.ReadAsync<WinUiSettingsRequest>(server, read.Token).ConfigureAwait(false);
                    Task<WinUiSettingsResponse> operation = await owner.Dispatcher.InvokeAsync(
                        () => owner.ApplyWinUiSettingsAsync(request, cancellation), DispatcherPriority.Normal, cancellation);
                    var response = await operation.ConfigureAwait(false);
                    await SettingsPipe.WriteAsync(server, response, cancellation).ConfigureAwait(false);
                    if (response.Error == null && request.Enabled == true &&
                        request.Setting is WinUiSetting.Restart or WinUiSetting.Exit)
                    {
                        _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                        {
                            owner.EndSessionFromSettings(request.Setting == WinUiSetting.Restart);
                        }));
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                catch (IOException) { }
                catch (InvalidDataException) { }
                catch (JsonException) { }
                finally
                {
                    if (server.IsConnected) server.Disconnect();
                }
            }
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(nint window);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(nint window, int command);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(nint window);
    }
}
