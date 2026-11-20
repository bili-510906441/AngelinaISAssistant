using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace AngelinaISAssistant;

/// <summary>
/// 通知区域（系统托盘）图标：热键隐藏主窗口后，程序仍可从屏幕右下角「^」里重新打开或退出。
/// 使用 Shell_NotifyIcon 直接实现，不引入 WinForms。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>托盘回调消息（WM_APP + 1），由主窗口的消息钩子处理。</summary>
    internal const int CallbackMessage = 0x8001;

    private const uint NIM_ADD = 0x0;
    private const uint NIM_DELETE = 0x2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint NotifyIconId = 1;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;

    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;

    public TrayIcon(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _icon = LoadApplicationIcon();
    }

    public bool IsShown => _added;

    /// <summary>把图标加入通知区域。返回是否成功。</summary>
    public bool Show(string tooltip)
    {
        if (_added)
        {
            return true;
        }

        if (_hwnd == IntPtr.Zero)
        {
            return false;
        }

        var data = CreateData(tooltip);

        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            Logger.Warn(
                $"tray icon add failed · icon=0x{_icon.ToInt64():X} size={data.cbSize} " +
                $"hwnd=0x{_hwnd.ToInt64():X} err={Marshal.GetLastWin32Error()}");
            return false;
        }

        _added = true;
        return true;
    }

    /// <summary>从通知区域移除图标（进程仍然存活，之后可以再次 Show）。</summary>
    public void Hide()
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData(string.Empty);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }

    public void Dispose()
    {
        Hide();

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private NOTIFYICONDATA CreateData(string tooltip) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = NotifyIconId,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = tooltip ?? string.Empty
    };

    /// <summary>
    /// 从内嵌的 app.ico 加载托盘图标。先把 ico 写到临时文件，再用 LoadImage 取图标，
    /// 这样在 exe 与 dotnet 宿主启动两种情况下都能拿到程序自己的图标。
    /// </summary>
    private static IntPtr LoadApplicationIcon()
    {
        string? temporary = null;

        try
        {
            var resource = Application.GetResourceStream(
                new Uri("pack://application:,,,/AngelinaISAssistant;component/Resources/app.ico"));

            if (resource?.Stream is null)
            {
                return IntPtr.Zero;
            }

            temporary = Path.Combine(Path.GetTempPath(), $"angelina-tray-{Environment.ProcessId}.ico");

            using (var file = File.Create(temporary))
            {
                resource.Stream.CopyTo(file);
            }

            var cx = GetSystemMetrics(SmCxSmIcon);
            var cy = GetSystemMetrics(SmCySmIcon);

            var icon = LoadImage(IntPtr.Zero, temporary, ImageIcon, cx, cy, LrLoadFromFile);

            if (icon == IntPtr.Zero)
            {
                Logger.Warn(
                    $"tray icon LoadImage failed · {cx}x{cy} err={Marshal.GetLastWin32Error()} " +
                    $"bytes={new FileInfo(temporary).Length}");
            }

            return icon;
        }
        catch (Exception ex)
        {
            Logger.Warn($"tray icon load failed · {ex.GetType().Name}: {ex.Message}");
            return IntPtr.Zero;
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    // 临时文件删不掉也无所谓
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
