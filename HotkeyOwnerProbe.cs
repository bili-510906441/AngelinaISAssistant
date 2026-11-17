using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace AngelinaISAssistant;

/// <summary>
/// Windows 没有公开“查询热键归属”的 API，这里用低级键盘钩子做近似定位：
/// 用户按下被占用的组合后，系统会把 WM_HOTKEY 交给占用它的程序，
/// 通常该程序会把自己带到前台，此时读取前台窗口的进程即可得知占用者。
/// </summary>
internal static class HotkeyOwnerProbe
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private static HookProc? _proc;
    private static IntPtr _hook;
    private static uint _vk;
    private static uint _mods;
    private static Action<int>? _onOwner;
    private static DispatcherTimer? _timeout;

    public static bool IsRunning => _hook != IntPtr.Zero;

    public static void Start(uint modifiers, uint vk, TimeSpan timeout, Action<int> onOwner)
    {
        Stop();

        _vk = vk;
        _mods = modifiers;
        _onOwner = onOwner;

        _proc = Callback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);

        if (_hook == IntPtr.Zero)
        {
            Logger.Warn("hotkey owner probe hook failed");
            return;
        }

        _timeout = new DispatcherTimer { Interval = timeout };
        _timeout.Tick += (_, _) => Stop();
        _timeout.Start();
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _timeout?.Stop();
        _timeout = null;
        _proc = null;
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var message = wParam.ToInt32();

                if (message is WmKeyDown or WmSysKeyDown)
                {
                    var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

                    if (info.VkCode == _vk && ModifiersMatch())
                    {
                        // 等占用程序处理完 WM_HOTKEY 再取前台窗口
                        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
                        delay.Tick += (_, _) =>
                        {
                            delay.Stop();
                            Capture();
                        };
                        delay.Start();
                    }
                }
            }
        }
        catch
        {
            // 钩子回调里不能抛异常
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool ModifiersMatch()
    {
        var ctrl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
        var shift = (GetAsyncKeyState(VkShift) & 0x8000) != 0;
        var alt = (GetAsyncKeyState(VkMenu) & 0x8000) != 0;

        return ctrl == ((_mods & 0x2) != 0)
               && shift == ((_mods & 0x4) != 0)
               && alt == ((_mods & 0x1) != 0);
    }

    private static void Capture()
    {
        var callback = _onOwner;
        Stop();

        if (callback is null)
        {
            return;
        }

        var pid = ForegroundProcessId();

        if (pid > 0)
        {
            callback(pid);
            
            
        }
    }

    private static int ForegroundProcessId()
    {
        var handle = GetForegroundWindow();
        return handle == IntPtr.Zero ? 0 : GetWindowThreadProcessId(handle, out var pid) == 0 ? 0 : pid;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
}
