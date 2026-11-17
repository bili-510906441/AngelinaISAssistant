using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;

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

    private const int MaxLogLines = 6;

    private static readonly Brush TimeBrush = Frozen(0x9A, 0xA3, 0xAE);
    private static readonly Brush InfoBrush = Frozen(0x3D, 0xDC, 0x84);
    private static readonly Brush WarnBrush = Frozen(0xFF, 0xD6, 0x0A);
    private static readonly Brush ErrorBrush = Frozen(0xFF, 0x6B, 0x6B);
    private static readonly Brush TextBrush = Frozen(0xE6, 0xE6, 0xE6);

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
        TitleText.Text = $"Angelina IS Assistant v{version}";

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
        }
        catch
        {
            // 设置失败时仍保持透明显示，只是可能拦截点击
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);
}
