using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Ink;
using System.Windows;
using System.Xml.Linq;

namespace FluentCanvas.Helpers;

internal sealed class FrozenInk : SafeHandleZeroOrMinusOneIsInvalid
{
    private FrozenInk() : base(true) { }
    private Guid pageId;
    private Dictionary<Guid, SceneObject> objects;
    internal SceneObject SceneFor(UIElement item) =>
        objects != null && objects.TryGetValue(SceneObject.ObjectId(item), out var found) ? found : SceneObject.Capture(item);
    internal void WritePageId(XElement root) => root.SetAttributeValue("PageId", pageId.ToString("D"));
    internal long Length { get; private init; }

    internal static FrozenInk Capture(NativeInkPage page, ulong[] selected = null)
    {
        nuint size;
        var handle = selected == null ? fc_core_freeze(page, out size)
            : fc_core_freeze_selection(page, selected, (nuint)selected.Length, out size);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Cannot freeze the ink page.");
        var frozen = new FrozenInk { Length = checked((long)size) };
        frozen.SetHandle(handle);
        try
        {
            frozen.pageId = page.PageId;
            if (selected == null) frozen.objects = page.ReadScene();
        }
        catch { frozen.Dispose(); throw; }
        return frozen;
    }

    internal void Read(long offset, byte[] buffer, int length)
    {
        if (offset < 0 || length < 0 || length > buffer.Length || offset > Length - length)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (fc_frozen_read(this, (nuint)offset, buffer, (nuint)length) != 0)
            throw new InvalidOperationException("Cannot read the frozen ink page.");
    }

    internal byte[] ExportInk()
    {
        var bytes = new byte[checked((int)Length)];
        Read(0, bytes, bytes.Length);
        return bytes;
    }
    internal int RecordLength(int index) => checked((int)fc_frozen_record_size(this, (nuint)index));

    internal IEnumerable<Stroke> ReadStrokes()
    {
        long offset = 0;
        for (nuint index = 0; offset < Length; index++)
        {
            int length = checked((int)fc_frozen_record_size(this, index));
            if (length <= 0) throw new InvalidOperationException("Invalid frozen stroke length.");
            var data = new byte[length];
            Read(offset, data, length);
            offset += length;
            yield return NativeStrokeCodec.Decode(data);
        }
    }

    protected override bool ReleaseHandle()
    {
        fc_frozen_free(handle);
        return true;
    }

    [DllImport("fluent_canvas_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr fc_core_freeze(NativeInkPage core, out nuint size);
    [DllImport("fluent_canvas_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr fc_core_freeze_selection(NativeInkPage core, ulong[] ids, nuint count, out nuint size);
    [DllImport("fluent_canvas_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint fc_frozen_record_size(FrozenInk snapshot, nuint index);
    [DllImport("fluent_canvas_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern int fc_frozen_read(FrozenInk snapshot, nuint offset, [Out] byte[] destination, nuint size);
    [DllImport("fluent_canvas_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern void fc_frozen_free(IntPtr snapshot);
}

public partial class TimeMachine
{
    internal FrozenInk FreezeInk() => FrozenInk.Capture(page);
    internal FrozenInk FreezeSelection(StrokeCollection selection) =>
        FrozenInk.Capture(page, selection.Select(NativeInkPage.Id).ToArray());
}

internal static class FrozenPageExtensions
{
    internal static FrozenInk FreezeInk(this TimeMachine.Snapshot page) => FrozenInk.Capture(page.Page);
}
