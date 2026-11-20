using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using AngelinaISAssistant.Live;

namespace AngelinaISAssistant;

/// <summary>
/// 只用于显示的全屏遮罩：透明、置顶、鼠标点击穿透到下层窗口。
/// 内容在左侧垂直居中，第一行为标题，下面最多 6 行独立日志（与程序的「输出日志」互不相干）。
/// </summary>
public partial class MaskOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    /// <summary>WDA_EXCLUDEFROMCAPTURE：窗口照常显示，但不会出现在屏幕采集/截图里。</summary>
    private const uint WdaExcludeFromCapture = 0x00000011;

    private const int MaxLogLines = 6;

    private readonly string _defaultTitle;

    /// <summary>本次采集已经追加过的提示（同一提示不重复追加，停止采集时清空）。</summary>
    private readonly HashSet<string> _shownMessages = new(StringComparer.Ordinal);

    private static readonly Brush TimeBrush = Frozen(0x9A, 0xA3, 0xAE);
    private static readonly Brush InfoBrush = Frozen(0x3D, 0xDC, 0x84);
    private static readonly Brush WarnBrush = Frozen(0xFF, 0xD6, 0x0A);
    private static readonly Brush ErrorBrush = Frozen(0xFF, 0x6B, 0x6B);
    private static readonly Brush TextBrush = Frozen(0xE6, 0xE6, 0xE6);

    /// <summary>敌人数值标注的颜色（金黄色，在深色面板上清晰）。</summary>
    private static readonly Brush StatValueBrush = Frozen(0xFF, 0xD5, 0x4A);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public MaskOverlayWindow()
    {
        InitializeComponent();

        var version = AppInfo.Version.TrimStart('v', 'V');
        _defaultTitle = $"AngelinaISAssistant v{version}";
        TitleText.Text = _defaultTitle;

        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        SourceInitialized += (_, _) => MakeClickThrough();
    }

    /// <summary>追加一行独立日志；超过 6 行时丢弃最早的一行。</summary>
    public void AppendLog(string level, string message)
    {
        var line = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };

        line.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = TimeBrush });
        line.Inlines.Add(new Run($"[{level}] ") { Foreground = BrushForLevel(level) });
        line.Inlines.Add(new Run(message) { Foreground = TextBrush });

        LogPanel.Children.Add(line);

        while (LogPanel.Children.Count > MaxLogLines)
        {
            LogPanel.Children.RemoveAt(0);
        }
    }

    private static Brush BrushForLevel(string level) => level switch
    {
        "WRN" => WarnBrush,
        "ERR" => ErrorBrush,
        _ => TextBrush
    };

    /// <summary>
    /// 更新遮罩内容：第一行固定为版本标题；左侧追加「[时间] [INF] 识别到关卡名 / 难度 / 已进入第几层」，
    /// 已经追加过的提示不重复追加；canvas 上按帧重绘敌人数值等标注（result 为 null 时清空全部）。
    /// </summary>
    public void UpdateOverlay(LiveAssistResult? result)
    {
        OverlayCanvas.Children.Clear();
        TitleText.Text = _defaultTitle;

        if (result is null)
        {
            LogPanel.Children.Clear();
            _shownMessages.Clear();
            return;
        }

        foreach (var item in result.Items)
        {
            var message = item.Text;

            if (string.IsNullOrWhiteSpace(message) || !_shownMessages.Add(message))
            {
                continue;
            }

            AppendLog("INF", message);
        }

        if (result.Annotations is { Count: > 0 } annotations)
        {
            DrawAnnotations(annotations);
        }
    }

    /// <summary>按帧绘制标注（敌人情报页的数值）：坐标是截图物理像素，换算成 DIP 后右对齐/自动缩放。</summary>
    private void DrawAnnotations(IReadOnlyList<OverlayAnnotation> annotations)
    {
        var dpi = VisualTreeHelper.GetDpi(this);

        foreach (var annotation in annotations)
        {
            if (string.IsNullOrWhiteSpace(annotation.Text))
            {
                continue;
            }

            var fontSize = annotation.FontSize / dpi.DpiScaleY;
            var maxWidth = annotation.MaxWidth / dpi.DpiScaleX;

            var text = new TextBlock
            {
                Text = annotation.Text,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushForAnnotation(annotation.Kind),
                TextWrapping = TextWrapping.NoWrap
            };

            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // 空间不够时按比例缩小字号，保证数字不会压到后面的字母等级
            if (maxWidth > 0 && text.DesiredSize.Width > maxWidth)
            {
                var ratio = maxWidth / Math.Max(1, text.DesiredSize.Width);
                text.FontSize = Math.Max(10, fontSize * ratio);
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }

            var x = annotation.X / dpi.DpiScaleX;
            var y = annotation.Y / dpi.DpiScaleY;

            if (annotation.AlignRight)
            {
                x -= text.DesiredSize.Width;
            }

            Canvas.SetLeft(text, x);
            Canvas.SetTop(text, y);
            OverlayCanvas.Children.Add(text);
        }
    }

    private static Brush BrushForAnnotation(OverlayKind kind) => kind switch
    {
        OverlayKind.EnemyStat => StatValueBrush,
        _ => TextBrush
    };

    private void MakeClickThrough()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;

            if (handle == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(handle, GwlExStyle);
            style |= WsExTransparent | WsExLayered | WsExNoActivate | WsExToolWindow;
            SetWindowLong(handle, GwlExStyle, style);

            // 不要把自己截进屏幕采集里：否则 OCR 会再次识别遮罩上的旧文字/置信度，
            // 形成自我识别循环。Win10 2004+ 的 WDA_EXCLUDEFROMCAPTURE 可让窗口照常显示但不出现在截图中。
            if (!SetWindowDisplayAffinity(handle, WdaExcludeFromCapture))
            {
                Logger.Warn($"overlay capture exclusion failed · err={Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            // 设置失败时仍保持透明显示，只是可能拦截点击
            Logger.Warn($"overlay window style failed · {ex.Message}");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);
}
