using System;

namespace AngelinaISAssistant.Live;

/// <summary>
/// 占位后端：识别管线尚未接入时保持全链路可运行（不产生任何悬浮内容）。
/// 接入 OpenCV / PaddleOCR-ONNX 时新增一个 ILiveAssistBackend 实现并替换即可。
/// </summary>
internal sealed class NoopLiveAssistBackend : ILiveAssistBackend
{
    public bool IsAvailable => false;

    public void Initialize()
    {
    }

    public LiveAssistResult? Process(CapturedFrame frame, LiveAssistOptions options) => null;

    public void Dispose()
    {
    }
}
