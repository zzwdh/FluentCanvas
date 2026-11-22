using System;
using System.ComponentModel;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace FluentCanvas.Helpers;

internal readonly record struct ForegroundWindowInfo(string ProcessName, string Title, int? Height, bool IsMicrosoftWhiteboard)
{
    internal static ForegroundWindowInfo? Capture(bool identifyMicrosoftWhiteboard)
    {
        var window = PInvoke.GetForegroundWindow();
        if (window.IsNull || PInvoke.GetWindowThreadProcessId(window, out uint processId) == 0) return null;

        string processName;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }

        Span<char> title = stackalloc char[256];
        int length = PInvoke.GetWindowText(window, title);
        int? height = PInvoke.GetWindowRect(window, out var bounds) ? bounds.bottom - bounds.top : null;
        bool isWhiteboard = identifyMicrosoftWhiteboard && HasWhiteboardIdentity(window, processId, processName);

        // The foreground window can be destroyed while its properties are being read.
        if (PInvoke.GetWindowThreadProcessId(window, out uint currentProcess) == 0 || currentProcess != processId)
            return null;

        return new ForegroundWindowInfo(processName, title[..length].ToString(), height, isWhiteboard);
    }

    private static bool HasWhiteboardIdentity(HWND frame, uint processId, string processName)
    {
        // A hosted UWP window carries the content process identity on its CoreWindow child.
        if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            var content = PInvoke.FindWindowEx(frame, default, "Windows.UI.Core.CoreWindow", null);
            if (content.IsNull || PInvoke.GetWindowThreadProcessId(content, out processId) == 0) return false;
        }

        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false, processId);
        if (process.IsInvalid) return false;

        Span<char> family = stackalloc char[128];
        uint capacity = (uint)family.Length;
        if (PInvoke.GetPackageFamilyName(process, ref capacity, family) != 0 || capacity <= 1) return false;

        return family[..((int)capacity - 1)] is "Microsoft.Whiteboard_8wekyb3d8bbwe"
            or "Microsoft.Office.Whiteboard_8wekyb3d8bbwe"
            or "Microsoft.Office.WhiteboardPreview_8wekyb3d8bbwe";
    }
}
