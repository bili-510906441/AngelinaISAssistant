namespace AngelinaISAssistant.Capture;

/// <summary>画面采集方式。</summary>
internal enum CaptureBackend
{
    /// <summary>Windows.Graphics.Capture（Windows 10 1903+）。</summary>
    Wgc,

    /// <summary>DXGI Desktop Duplication（桌面复制，Windows 8+）。</summary>
    Dxgi,

    /// <summary>BitBlt（GDI 屏幕拷贝，兼容性最好）。</summary>
    BitBlt
}
