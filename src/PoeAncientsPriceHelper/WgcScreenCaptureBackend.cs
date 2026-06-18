using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace PoeAncientsPriceHelper;

// Windows Graphics Capture (WGC) backend — the modern GPU-based screen capture API.
// Captures the entire monitor via a persistent GraphicsCaptureSession, then crops the
// requested region on the CPU side from a staging texture readback.
//
// On any failure (unsupported OS, device lost, access denied, null frame), it silently
// falls back to GDI for that call so the scan loop never breaks.
internal sealed class WgcScreenCaptureBackend : IScreenCaptureBackend
{
    private readonly GdiScreenCaptureBackend _fallback = new();

    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDirect3DDevice? _winrtDevice;

    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _framePoolSize;

    private IntPtr _currentHMonitor = IntPtr.Zero;
    private Rectangle _monitorBounds;

    private bool _initialized;
    private bool _wgcFailed;

    public Bitmap CaptureRegion(Rectangle region)
    {
        if (_wgcFailed)
            return _fallback.CaptureRegion(region);

        try
        {
            EnsureDevice();
            EnsureSession(region);
        }
        catch (Exception)
        {
            // If WGC cannot even initialize/start a monitor session, treat it as unavailable
            // for this process. Avoid retrying D3D/WinRT setup on every scan tick (~10 Hz).
            DisableWgc();
            return _fallback.CaptureRegion(region);
        }

        try
        {

            var frame = _framePool?.TryGetNextFrame();
            if (frame is null)
                return _fallback.CaptureRegion(region);

            Bitmap bitmap;
            SizeInt32 contentSize;
            using (frame)
            {
                contentSize = frame.ContentSize;
                bitmap = CropFrameToBitmap(frame, region);
            }

            try { RecreateFramePoolIfNeeded(contentSize); }
            catch
            {
                bitmap.Dispose();
                throw;
            }
            return bitmap;
        }
        catch (Exception)
        {
            // WGC failed for this call — fall back to GDI without killing the backend
            // (transient errors like a null frame during monitor transition recover next call).
            return _fallback.CaptureRegion(region);
        }
    }

    // ── Device + session lifecycle ──────────────────────────────────────────

    private void EnsureDevice()
    {
        if (_initialized) return;

        // Create a D3D11 hardware device with BGRA support (required by WGC).
        _d3dDevice = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        _d3dContext = _d3dDevice.ImmediateContext;

        // Bridge the D3D11 device to the WinRT IDirect3DDevice that WGC expects.
        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr ptr);
        if (hr != 0)
            throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr:X8}");
        try { _winrtDevice = Marshal.GetObjectForIUnknown(ptr) as IDirect3DDevice; }
        finally { Marshal.Release(ptr); }

        if (_winrtDevice is null)
            throw new InvalidOperationException("Failed to create WinRT IDirect3DDevice");

        _initialized = true;
    }

    private void EnsureSession(Rectangle region)
    {
        // Find the monitor that contains the capture region.
        var nativeRect = new RECT { Left = region.X, Top = region.Y, Right = region.Right, Bottom = region.Bottom };
        var hMonitor = MonitorFromRect(ref nativeRect, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero)
            throw new InvalidOperationException("MonitorFromRect returned null");

        // (Re)create the capture session when the monitor changes.
        if (hMonitor == _currentHMonitor && _framePool is not null) return;

        DisposeSession();
        _currentHMonitor = hMonitor;

        // Query monitor bounds so we can map absolute screen coords → frame-local coords.
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref info))
            throw new InvalidOperationException("GetMonitorInfo failed");
        _monitorBounds = new Rectangle(
            info.rcMonitor.Left, info.rcMonitor.Top,
            info.rcMonitor.Right - info.rcMonitor.Left,
            info.rcMonitor.Bottom - info.rcMonitor.Top);

        // Create the GraphicsCaptureItem for this monitor (via the interop interface,
        // since there's no public managed constructor for monitor-based items).
        _captureItem = CreateItemForMonitor(hMonitor);
        if (_captureItem is null)
            throw new InvalidOperationException("CreateItemForMonitor returned null");

        // Create a free-threaded frame pool (no Dispatcher required) and start capturing.
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice!,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            _captureItem.Size);
        _framePoolSize = _captureItem.Size;

        _session = _framePool.CreateCaptureSession(_captureItem);
        _session.IsCursorCaptureEnabled = false;
        _session.StartCapture();
    }

    // ── Frame → Bitmap ──────────────────────────────────────────────────────

    private Bitmap CropFrameToBitmap(Direct3D11CaptureFrame frame, Rectangle region)
    {
        // The frame's Surface is a WinRT IDirect3DSurface backed by a D3D11 texture.
        // Use IDirect3DDxgiInterfaceAccess to QI for the raw ID3D11Texture2D.
        var access = (IDirect3DDxgiInterfaceAccess)frame.Surface;
        var iidTex = IID_ID3D11Texture2D;
        var texturePtr = access.GetInterface(ref iidTex);
        using var frameTexture = new ID3D11Texture2D(texturePtr);

        // Copy the GPU texture to a CPU-readable staging texture.
        var desc = frameTexture.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = _d3dDevice!.CreateTexture2D(desc);
        _d3dContext!.CopyResource(staging, frameTexture);

        // Map the staging texture and crop the requested region.
        var map = _d3dContext.Map(staging, 0, MapMode.Read);
        try
        {
            // Frame coords are relative to the monitor's top-left corner.
            int relX = region.X - _monitorBounds.X;
            int relY = region.Y - _monitorBounds.Y;
            int w = region.Width;
            int h = region.Height;

            // Clamp to frame bounds (the monitor might have resized since calibration).
            int frameWidth = frame.ContentSize.Width;
            int frameHeight = frame.ContentSize.Height;
            if (frameWidth <= 0 || frameHeight <= 0)
                throw new InvalidOperationException("Frame has invalid size");

            relX = Math.Clamp(relX, 0, frameWidth - 1);
            relY = Math.Clamp(relY, 0, frameHeight - 1);
            w = Math.Min(w, frameWidth - relX);
            h = Math.Min(h, frameHeight - relY);
            if (w <= 0 || h <= 0)
                throw new InvalidOperationException("Region outside frame bounds");

            // WGC delivers BGRA (32-bit). Create a 32bpp RGB bitmap and copy row-by-row
            // (source stride may differ from destination stride).
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            try
            {
                var bmpData = bmp.LockBits(
                    new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppRgb);
                try
                {
                    int srcStride = (int)map.RowPitch;
                    int dstStride = bmpData.Stride;
                    IntPtr srcRow = map.DataPointer + relY * srcStride + relX * 4;
                    var rowBuffer = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(srcRow + y * srcStride, rowBuffer, 0, rowBuffer.Length);
                        Marshal.Copy(rowBuffer, 0, bmpData.Scan0 + y * dstStride, rowBuffer.Length);
                    }
                }
                finally { bmp.UnlockBits(bmpData); }

                return bmp;
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
        }
        finally { _d3dContext.Unmap(staging, 0); }
    }

    private void RecreateFramePoolIfNeeded(SizeInt32 contentSize)
    {
        if (_framePool is null || _winrtDevice is null)
            return;
        if (contentSize.Width <= 0 || contentSize.Height <= 0)
            return;
        if (contentSize.Width == _framePoolSize.Width && contentSize.Height == _framePoolSize.Height)
            return;

        _framePool.Recreate(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            contentSize);
        _framePoolSize = contentSize;
    }

    // ── GraphicsCaptureItem creation via WinRT interop ──────────────────────

    private static GraphicsCaptureItem? CreateItemForMonitor(IntPtr hMonitor)
    {
        // GraphicsCaptureItem has no public constructor for monitor-based capture.
        // We must go through the IGraphicsCaptureItemInterop COM interface, obtained
        // from the GraphicsCaptureItem activation factory via RoGetActivationFactory.
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, (uint)className.Length, out var hString);
        try
        {
            var iidFactory = IID_IGraphicsCaptureItemInterop;
            var hr = RoGetActivationFactory(hString, ref iidFactory, out IntPtr factoryPtr);
            if (hr != 0) return null;
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                var iidItem = IID_IGraphicsCaptureItem;
                var itemHr = interop.CreateForMonitor(hMonitor, ref iidItem, out var itemPtr);
                if (itemHr != 0 || itemPtr == IntPtr.Zero)
                    return null;
                try { return Marshal.GetObjectForIUnknown(itemPtr) as GraphicsCaptureItem; }
                finally { Marshal.Release(itemPtr); }
            }
            finally { Marshal.Release(factoryPtr); }
        }
        finally { WindowsDeleteString(hString); }
    }

    // ── Dispose ─────────────────────────────────────────────────────────────

    private void DisposeSession()
    {
        _session?.Dispose();
        _framePool?.Dispose();
        _captureItem = null;
        _framePool = null;
        _session = null;
        _framePoolSize = default;
    }

    private void DisableWgc()
    {
        _wgcFailed = true;
        DisposeSession();
        DisposeDevice();
        _currentHMonitor = IntPtr.Zero;
    }

    private void DisposeDevice()
    {
        _d3dContext?.Dispose();
        _d3dDevice?.Dispose();
        _winrtDevice = null;
        _d3dContext = null;
        _d3dDevice = null;
        _initialized = false;
    }

    public void Dispose()
    {
        DisposeSession();
        DisposeDevice();
        _fallback.Dispose();
    }

    // ── COM interop interfaces ──────────────────────────────────────────────

    // Bridges WinRT IDirect3DSurface → raw DXGI/D3D11 interfaces.
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    // Factory interface for creating GraphicsCaptureItem from an HMONITOR.
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow([In] IntPtr window, [In] ref Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid, out IntPtr result);
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────

    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out IntPtr hString);

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsDeleteString(IntPtr hString);

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
