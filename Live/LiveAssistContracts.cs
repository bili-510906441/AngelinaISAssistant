using System;
using System.Collections.Generic;

namespace AngelinaISAssistant.Live;

/// <summary>实时辅助左侧勾选项；后端据此决定跑哪些识别流程。</summary>
[Flags]
public enum LiveAssistOptions
{
    None = 0,
    RelicRecord = 1 << 0,      // 局外：收藏品记录
    StageValues = 1 << 1,      // 局外：关卡数值计算
    BattleAssist = 1 << 2,     // 局内：实时战斗辅助
    EnemyHealth = 1 << 3,      // 局内：敌人血量检测
    SpecialStage = 1 << 4      // 局内：特殊关卡详细辅助
}

/// <summary>一帧屏幕画面（BGRA32，物理像素）。</summary>
public readonly record struct CapturedFrame(byte[] Pixels, int Width, int Height);

public enum OverlayKind
{
    Info,
    Warning,
    EnemyHealth,
    StageValue,
    Relic,

    /// <summary>敌人情报页上按帧绘制的数值标注。</summary>
    EnemyStat
}

/// <summary>
/// 悬浮窗左侧提示的一行文字（追加式日志）。当前版本只显示语义提示，
/// 不再画识别框；X/Y/Width/Height 仅为兼容保留，语义提示使用 0。
/// </summary>
public sealed record OverlayItem(
    OverlayKind Kind,
    string Text,
    double X,
    double Y,
    double Width = 0,
    double Height = 0);

/// <summary>
/// 按帧重绘的遮罩标注（例如「敌人情报」页里显示在属性标签右侧的数值）。
/// 坐标是截图帧的物理像素，由遮罩换算成 DIP 后绘制。
/// </summary>
public sealed record OverlayAnnotation(
    string Text,
    double X,
    double Y,
    double FontSize,
    double MaxWidth = 0,
    bool AlignRight = false,
    OverlayKind Kind = OverlayKind.EnemyStat);

/// <summary>后端一次推理的输出；null 表示清空悬浮内容。</summary>
public sealed record LiveAssistResult(
    IReadOnlyList<OverlayItem> Items,
    IReadOnlyList<OverlayAnnotation>? Annotations = null,
    string? Status = null);

/// <summary>
/// 识别后端契约。OpenCV / PaddleOCR-ONNX 的实现只需实现本接口，
/// 前端（勾选、截图、悬浮窗）无需改动。
/// </summary>
public interface ILiveAssistBackend : IDisposable
{
    /// <summary>当前环境是否具备运行条件（模型/原生库是否就绪）。</summary>
    bool IsAvailable { get; }

    /// <summary>加载模型、初始化环境；在后台线程调用。</summary>
    void Initialize();

    /// <summary>处理一帧；在后台线程调用，可能被高频调用，需自行控制耗时。</summary>
    LiveAssistResult? Process(CapturedFrame frame, LiveAssistOptions options);
}
