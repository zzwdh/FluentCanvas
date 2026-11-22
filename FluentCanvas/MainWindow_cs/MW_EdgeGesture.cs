using System.ComponentModel;
using System.Windows.Interop;
using FluentCanvas.Helpers;

namespace FluentCanvas;

public partial class MainWindow
{
    private bool edgeGestureOverrideApplied;

    internal void ShellSetEdgeGestureUtil(bool enabled) => ApplyEdgeGestureOverride(enabled);

    private bool ApplyEdgeGestureOverride(bool enabled)
    {
        if (enabled == edgeGestureOverrideApplied) return true;
        if (!EdgeGestureUtil.SetOverride(new WindowInteropHelper(this).Handle, enabled)) return false;
        edgeGestureOverrideApplied = enabled;
        return true;
    }

    private void ClearEdgeGestureOverride() => ShellSetEdgeGestureUtil(false);

    protected override void OnClosing(CancelEventArgs args)
    {
        base.OnClosing(args);
        if (!args.Cancel) ClearEdgeGestureOverride();
    }
}
