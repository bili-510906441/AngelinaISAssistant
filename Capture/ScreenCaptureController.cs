using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace AngelinaISAssistant.Capture;

/// <summary>
/// 基于 Windows.Graphics.Capture（Windows 10 1903+，OBS 里的「Windows 10 采集」同款接口）
/// 的屏幕/窗口采集，把帧转成 BGRA 字节交给界面显示。
/// </summary>
internal sealed class ScreenCaptureController : IDisposable
{
    private const int D3DDriverTypeHardware = 1;
    private const int D3DDriverTypeWarp = 5;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;
    private const uint MonitorDefaultToPrimary = 1;

    /// <summary>IID_IDXGIDevice。</summary>
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary>IID_GraphicsCaptureItem。</summary>
    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>IID_IActivationFactory。</summary>
    private static readonly Guid ActivationFactoryId = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    /// <summary>
    /// 直接对主显示器（显示器 0）建采集项，不弹系统选择器。
    /// 先用主显示器的 HMONITOR，失败再依次尝试枚举到的其它显示器。
    /// </summary>
    public static GraphicsCaptureItem CreatePrimaryMonitorItem()
    {
        var candidates = new List<IntPtr>();

        var primary = MonitorFromPoint(new NativePoint { X = 0, Y = 0 }, MonitorDefaultToPrimary);
        if (primary != IntPtr.Zero)
        {
            candidates.Add(primary);
        }

        foreach (var monitor in EnumerateMonitors())
        {
            if (!candidates.Contains(monitor))
            {
                candidates.Add(monitor);
            }
        }

        var lastError = (Exception?)null;

        foreach (var monitor in candidates)
        {
            try
            {
                return CreateItemForMonitor(monitor);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        // 不包装成 InvalidOperationException，保留原始 HRESULT，否则会被 0x80131509 盖住
        if (lastError is not null)
        {
            ExceptionDispatchInfo.Capture(lastError).Throw();
        }

        throw new InvalidOperationException("没有枚举到任何显示器。");
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        var interop = GetItemInterop();

        var iid = GraphicsCaptureItemId;
        var itemPointer = interop.CreateForMonitor(monitor, ref iid);

        try
        {
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    private static List<IntPtr> EnumerateMonitors()
    {
        var monitors = new List<IntPtr>();

        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            monitors.Add(monitor);
            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return monitors;
    }

    /// <summary>对指定窗口建采集项（诊断用）。</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr window)
    {
        var interop = GetItemInterop();

        var iid = GraphicsCaptureItemId;
        var itemPointer = interop.CreateForWindow(window, ref iid);

        try
        {
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    /// <summary>拿 GraphicsCaptureItem 的激活工厂并转成互操作接口。</summary>
    private static IGraphicsCaptureItemInterop GetItemInterop()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";

        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));

        try
        {
            // 先用 IActivationFactory 拿工厂，再 QueryInterface 到互操作接口；
            // 直接用互操作接口的 IID 去请求工厂会返回 E_INVALIDARG。
            var factoryId = ActivationFactoryId;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, in factoryId, out var factory));

            try
            {
                var interopId = typeof(IGraphicsCaptureItemInterop).GUID;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory, in interopId, out var interopPointer));

                try
                {
                    return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPointer);
                }
                finally
                {
                    Marshal.Release(interopPointer);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private readonly Action<byte[], int, int> _frameReady;
    private readonly Action<Exception> _failed;

    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;

    private double _frameIntervalMs = 100.0;
    private long _lastTimestamp;

    public ScreenCaptureController(Action<byte[], int, int> frameReady, Action<Exception> failed)
    {
        _frameReady = frameReady;
        _failed = failed;
    }

    public bool IsRunning => _session is not null;

    public int Fps { get; private set; } = 10;

    public void SetFps(int fps)
    {
        Fps = Math.Clamp(fps, 1, 60);
        _frameIntervalMs = 1000.0 / Fps;
    }

    public void Start(GraphicsCaptureItem item, int fps)
    {
        Stop();

        SetFps(fps);
        _item = item;
        _device = CreateDevice();

        _framePool = Direct3D11CaptureFramePool.Create(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);

        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = true;

        // IsBorderRequired 只有 Windows 11 才有，存在才设置（关掉采集边框）
        _session.GetType().GetProperty("IsBorderRequired")?.SetValue(_session, false);

        _session.StartCapture();

        _item.Closed += OnItemClosed;
    }

    public void Stop()
    {
        try
        {
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }

            if (_item is not null)
            {
                _item.Closed -= OnItemClosed;
            }

            _session?.Dispose();
            _framePool?.Dispose();
        }
        catch
        {
            // 停止过程中的异常不影响界面状态
        }

        _session = null;
        _framePool = null;
        _item = null;
        _device = null;
        _lastTimestamp = 0;
    }

    public void Dispose() => Stop();

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => Stop();

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            using (frame)
            {
                var timestamp = Stopwatch.GetTimestamp();
                if (_lastTimestamp != 0)
                {
                    var elapsedMs = (timestamp - _lastTimestamp) * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs < _frameIntervalMs)
                    {
                        return; // 按目标帧率丢帧
                    }
                }

                _lastTimestamp = timestamp;

                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                var width = bitmap.PixelWidth;
                var height = bitmap.PixelHeight;
                if (width <= 0 || height <= 0)
                {
                    return;
                }

                var length = width * height * 4;
                var buffer = new Windows.Storage.Streams.Buffer((uint)length);
                bitmap.CopyToBuffer(buffer);

                var pixels = new byte[length];
                using (var reader = DataReader.FromBuffer(buffer))
                {
                    reader.ReadBytes(pixels);
                }

                _frameReady(pixels, width, height);
            }
        }
        catch (Exception ex)
        {
            _failed(ex);
        }
    }

    /// <summary>创建 Direct3D11 设备，并包装成 WinRT 的 IDirect3DDevice。</summary>
    private static IDirect3DDevice CreateDevice()
    {
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3DDriverTypeHardware,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3D11SdkVersion,
            out var device,
            out _,
            out var context);

        if (hr < 0)
        {
            hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3DDriverTypeWarp,
                IntPtr.Zero,
                D3D11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3D11SdkVersion,
                out device,
                out _,
                out context);
        }

        Marshal.ThrowExceptionForHR(hr);

        try
        {
            var dxgiDeviceId = DxgiDeviceId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in dxgiDeviceId, out var dxgiDevice));

            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));

                try
                {
                    return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(device);

            if (context != IntPtr.Zero)
            {
                Marshal.Release(context);
            }
        }
    }

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors", ExactSpelling = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr monitorRect, IntPtr data);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>从 HMONITOR / HWND 直接创建 GraphicsCaptureItem 的 WinRT 互操作接口。</summary>
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }
}
