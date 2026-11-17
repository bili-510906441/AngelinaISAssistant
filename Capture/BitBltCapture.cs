using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AngelinaISAssistant.Capture;

/// <summary>BitBlt（GDI）主显示器采集，兼容性最好，但性能低于 DXGI / WGC。</summary>
internal sealed class BitBltCapture : IDisposable
{
    private const int SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    private readonly Action<byte[], int, int> _frameReady;
    private readonly Action<Exception> _failed;

    private Thread? _thread;
    private volatile bool _running;
    private int _fps = 10;

    public BitBltCapture(Action<byte[], int, int> frameReady, Action<Exception> failed)
    {
        _frameReady = frameReady;
        _failed = failed;
    }

    public bool IsRunning => _running;

    public int Fps => _fps;

    public void Start(int fps)
    {
        if (_running)
        {
            return;
        }

        _fps = Math.Clamp(fps, 1, 60);
        _running = true;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "BitBltCapture"
        };

        _thread.Start();
    }

    public void SetFps(int fps) => _fps = Math.Clamp(fps, 1, 60);

    public void Stop()
    {
        _running = false;
        _thread?.Join(800);
        _thread = null;
    }

    private void Loop()
    {
        var screen = GetSystemMetrics(SystemMetricPrimaryWidth);
        var height = GetSystemMetrics(SystemMetricPrimaryHeight);

        if (screen <= 0 || height <= 0)
        {
            Fail(new InvalidOperationException("无法获取主显示器尺寸。"));
            return;
        }

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldObject = IntPtr.Zero;
        var buffer = new byte[screen * height * 4];
        var watch = new Stopwatch();

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memoryDc = CreateCompatibleDC(screenDc);

            var info = new BitmapInfo
            {
                bmiHeader = new BitmapInfoHeader
                {
                    biSize = Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = screen,
                    biHeight = -height, // 负数表示自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BiRgb
                }
            };

            bitmap = CreateDIBSection(memoryDc, ref info, DibRgbColors, out var bits, IntPtr.Zero, 0);

            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                Fail(new InvalidOperationException("无法创建 DIB 缓冲区。"));
                return;
            }

            oldObject = SelectObject(memoryDc, bitmap);

            while (_running)
            {
                watch.Restart();

                if (!BitBlt(memoryDc, 0, 0, screen, height, screenDc, 0, 0, SrcCopy))
                {
                    Fail(new InvalidOperationException("BitBlt 调用失败。"));
                    return;
                }

                Marshal.Copy(bits, buffer, 0, buffer.Length);
                _frameReady((byte[])buffer.Clone(), screen, height);

                var target = 1000.0 / Math.Max(1, _fps);
                var elapsed = watch.Elapsed.TotalMilliseconds;

                if (elapsed < target)
                {
                    Thread.Sleep((int)(target - elapsed));
                }
            }
        }
        catch (Exception ex)
        {
            if (_running)
            {
                Fail(ex);
            }
        }
        finally
        {
            if (oldObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                SelectObject(memoryDc, oldObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    private void Fail(Exception exception)
    {
        _running = false;
        _failed(exception);
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destDc, int x, int y, int width, int height, IntPtr srcDc, int srcX, int srcY, int rop);

    private const int SystemMetricPrimaryWidth = 0;
    private const int SystemMetricPrimaryHeight = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}
