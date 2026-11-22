using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace FluentCanvas.Helpers;

internal static partial class WindowCapture
{
    private const uint Hardware = 1;
    private const uint Warp = 5;
    private const uint BgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;
    private const long MaxPixels = 8192L * 4320;
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMilliseconds(1250);
    private static readonly Guid CaptureItemId = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    internal static async Task<BitmapSource> CaptureAsync(nint window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            throw new PlatformNotSupportedException("Capturing a specific window requires Windows 10 version 1903 or later.");

        // Keep graphics device creation, frame delivery and readback independent of
        // the WPF dispatcher, including when Office is waiting for SlideLeaving.
        using var deadline = new CancellationTokenSource(CaptureTimeout);
        var cancellation = deadline.Token;
        return await Task.Run(() => ReadWindowAsync(window, cancellation), cancellation)
            .WaitAsync(cancellation).ConfigureAwait(false);
    }

    private static async Task<BitmapSource> ReadWindowAsync(nint window, CancellationToken cancellation)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows Graphics Capture is unavailable.");

        var interop = GraphicsCaptureItem.As<ICaptureItemInterop>();
        Marshal.ThrowExceptionForHR(interop.CreateForWindow(window, CaptureItemId, out nint itemPointer));
        GraphicsCaptureItem item;
        try { item = GraphicsCaptureItem.FromAbi(itemPointer); }
        finally { Marshal.Release(itemPointer); }

        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > MaxPixels)
            throw new InvalidOperationException("The slide window has an unsupported capture size.");

        using var device = CreateDevice();
        cancellation.ThrowIfCancellationRequested();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
        using var session = pool.CreateCaptureSession(item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            session.IsCursorCaptureEnabled = false;
        var arrival = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<Direct3D11CaptureFramePool, object> frameArrived = (_, _) => arrival.TrySetResult();
        TypedEventHandler<GraphicsCaptureItem, object> windowClosed = (_, _) =>
            arrival.TrySetException(new InvalidOperationException("The slide window closed during capture."));
        pool.FrameArrived += frameArrived;
        item.Closed += windowClosed;
        try
        {
            session.StartCapture();
            await arrival.Task.WaitAsync(cancellation).ConfigureAwait(false);
            using var frame = pool.TryGetNextFrame() ?? throw new InvalidOperationException("Windows supplied no capture frame.");
            var content = frame.ContentSize;
            if (content.Width <= 0 || content.Height <= 0 || content.Width > size.Width || content.Height > size.Height)
                throw new InvalidOperationException("The slide window resized during capture.");

            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore)
                .AsTask(cancellation).ConfigureAwait(false);
            int stride = checked(bitmap.PixelWidth * 4);
            var pixels = new byte[checked(stride * bitmap.PixelHeight)];
            bitmap.CopyToBuffer(pixels.AsBuffer());
            var result = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96,
                PixelFormats.Bgr32, null, pixels, stride);
            if (content.Width != result.PixelWidth || content.Height != result.PixelHeight)
                result = new CroppedBitmap(result, new Int32Rect(0, 0, content.Width, content.Height));
            result.Freeze();
            return result;
        }
        finally
        {
            item.Closed -= windowClosed;
            pool.FrameArrived -= frameArrived;
        }
    }

    private static IDirect3DDevice CreateDevice()
    {
        int hr = D3D11CreateDevice(0, Hardware, 0, BgraSupport, 0, 0, D3D11SdkVersion, out nint device, 0, 0);
        if (hr < 0)
            hr = D3D11CreateDevice(0, Warp, 0, BgraSupport, 0, 0, D3D11SdkVersion, out device, 0, 0);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, DxgiDeviceId, out nint dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out nint inspectable));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(device); }
    }

    // windows.graphics.capture.interop.h, IGraphicsCaptureItemInterop. The returned
    // pointer is the WinRT IGraphicsCaptureItem ABI, projected above by C#/WinRT.
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint hwnd, in Guid id, out nint result);
    }

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, nint featureLevel, nint immediateContext);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
