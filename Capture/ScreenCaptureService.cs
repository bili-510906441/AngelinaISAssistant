using System;

namespace AngelinaISAssistant.Capture;

/// <summary>按用户选择切换采集后端（WGC / DXGI / BitBlt），对界面暴露统一接口。</summary>
internal sealed class ScreenCaptureService : IDisposable
{
    private readonly Action<byte[], int, int> _frameReady;
    private readonly Action<Exception> _failed;

    private ScreenCaptureController? _wgc;
    private DxgiCapture? _dxgi;
    private BitBltCapture? _bitBlt;

    private CaptureBackend _backend = CaptureBackend.Wgc;
    private int _fps = 10;

    public ScreenCaptureService(Action<byte[], int, int> frameReady, Action<Exception> failed)
    {
        _frameReady = frameReady;
        _failed = failed;
    }

    public CaptureBackend Backend => _backend;

    public int Fps => _fps;

    public bool IsRunning =>
        _backend switch
        {
            CaptureBackend.Wgc => _wgc is { IsRunning: true },
            CaptureBackend.Dxgi => _dxgi is { IsRunning: true },
            CaptureBackend.BitBlt => _bitBlt is { IsRunning: true },
            _ => false
        };

    public void Start(CaptureBackend backend, int fps)
    {
        Stop();

        _backend = backend;
        _fps = Math.Clamp(fps, 1, 60);

        switch (backend)
        {
            case CaptureBackend.Dxgi:
                _dxgi ??= new DxgiCapture(_frameReady, _failed);
                _dxgi.Start(_fps);
                break;

            case CaptureBackend.BitBlt:
                _bitBlt ??= new BitBltCapture(_frameReady, _failed);
                _bitBlt.Start(_fps);
                break;

            default:
                _wgc ??= new ScreenCaptureController(_frameReady, _failed);
                _wgc.Start(ScreenCaptureController.CreatePrimaryMonitorItem(), _fps);
                break;
        }
    }

    public void SetFps(int fps)
    {
        _fps = Math.Clamp(fps, 1, 60);

        switch (_backend)
        {
            case CaptureBackend.Dxgi:
                _dxgi?.SetFps(_fps);
                break;
            case CaptureBackend.BitBlt:
                _bitBlt?.SetFps(_fps);
                break;
            default:
                _wgc?.SetFps(_fps);
                break;
        }
    }

    public void Stop()
    {
        _wgc?.Stop();
        _dxgi?.Stop();
        _bitBlt?.Stop();
    }

    public void Dispose()
    {
        _wgc?.Dispose();
        _dxgi?.Dispose();
        _bitBlt?.Dispose();

        _wgc = null;
        _dxgi = null;
        _bitBlt = null;
    }
}
