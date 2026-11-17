using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AngelinaISAssistant.Capture;

/// <summary>DXGI Desktop Duplication（桌面复制）主显示器采集。</summary>
internal sealed class DxgiCapture : IDisposable
{
    private const int WaitTimeoutHResult = unchecked((int)0x887A0027);
    private const int AccessLostHResult = unchecked((int)0x887A0022);

    private readonly Action<byte[], int, int> _frameReady;
    private readonly Action<Exception> _failed;

    private Thread? _thread;
    private volatile bool _running;
    private int _fps = 10;

    public DxgiCapture(Action<byte[], int, int> frameReady, Action<Exception> failed)
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
            Name = "DxgiCapture"
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
        ID3D11Device? device = null;
        IDXGIOutputDuplication? duplication = null;
        ID3D11Texture2D? staging = null;
        var watch = new Stopwatch();

        try
        {
            device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, null!);

            int width;
            int height;

            using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiDevice.GetAdapter())
            {
                var enumResult = adapter.EnumOutputs(0, out var output);

                if (!enumResult.Success || output is null)
                {
                    Fail(new InvalidOperationException("找不到可用的显示输出。"));
                    return;
                }

                using (output)
                using (var output1 = output.QueryInterface<IDXGIOutput1>())
                {
                    var bounds = output.Description.DesktopCoordinates;
                    width = bounds.Right - bounds.Left;
                    height = bounds.Bottom - bounds.Top;

                    if (width <= 0 || height <= 0)
                    {
                        Fail(new InvalidOperationException("无法获取显示器尺寸。"));
                        return;
                    }

                    duplication = output1.DuplicateOutput(device);
                }
            }

            var description = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            staging = device.CreateTexture2D(description);

            var context = device.ImmediateContext;
            var buffer = new byte[width * height * 4];
            var rowBytes = width * 4;

            while (_running)
            {
                watch.Restart();

                IDXGIResource? desktopResource = null;

                try
                {
                    var result = duplication.AcquireNextFrame(500, out _, out desktopResource);

                    if (!result.Success)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex.HResult == WaitTimeoutHResult)
                {
                    continue;
                }
                catch (Exception ex) when (ex.HResult == AccessLostHResult)
                {
                    Fail(new InvalidOperationException("桌面复制会话已失效（可能切换了分辨率或锁屏）。", ex));
                    return;
                }

                if (desktopResource is null)
                {
                    continue;
                }

                using (desktopResource)
                using (var texture = desktopResource.QueryInterface<ID3D11Texture2D>())
                {
                    context.CopyResource(staging, texture);
                }

                duplication.ReleaseFrame();

                var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                try
                {
                    var rowPitch = (int)mapped.RowPitch;

                    for (var y = 0; y < height; y++)
                    {
                        Marshal.Copy(mapped.DataPointer + (y * rowPitch), buffer, y * rowBytes, rowBytes);
                    }
                }
                finally
                {
                    context.Unmap(staging, 0);
                }

                _frameReady((byte[])buffer.Clone(), width, height);

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
            staging?.Dispose();
            duplication?.Dispose();
            device?.Dispose();
        }
    }

    private void Fail(Exception exception)
    {
        _running = false;
        _failed(exception);
    }

    public void Dispose() => Stop();
}
