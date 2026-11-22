using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace FluentCanvas.Helpers;

internal static class EdgeGestureUtil
{
    internal static bool SetOverride(nint window, bool disableGestures)
    {
        if (window == 0) return false;

        IPropertyStore store = null;
        try
        {
            var result = PInvoke.SHGetPropertyStoreForWindow((HWND)window, out store);
            if (result.Failed)
            {
                LogHelper.WriteLogToFile($"Could not open the window property store: {result}", LogHelper.LogType.Error);
                return false;
            }

            PROPVARIANT property = default; // VT_EMPTY removes the property from the window.
            if (disableGestures)
            {
                property.vt = VARENUM.VT_BOOL;
                property.boolVal = true;
            }
            store.SetValue(PInvoke.PKEY_EdgeGesture_DisableTouchWhenFullscreen, property);
            return true;
        }
        catch (Exception exception)
        {
            LogHelper.WriteLogToFile($"Could not update the full-screen edge gesture property: {exception}",
                LogHelper.LogType.Error);
            return false;
        }
        finally
        {
            if (store != null && Marshal.IsComObject(store)) Marshal.ReleaseComObject(store);
        }
    }
}
