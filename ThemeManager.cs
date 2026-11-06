using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AngelinaISAssistant;

internal static class ThemeManager
{
    public static bool IsDark { get; private set; }

    public static void Apply(bool dark)
    {
        IsDark = dark;

        if (Application.Current is null)
        {
            return;
        }

        var dictionary = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };

        var merged = Application.Current.Resources.MergedDictionaries;

        // 只替换主题字典，保留字符串等其它合并字典
        var existing = merged.FirstOrDefault(
            item => item.Source?.OriginalString?.Contains("Themes/", StringComparison.Ordinal) == true);

        if (existing is not null)
        {
            merged.Remove(existing);
        }

        merged.Add(dictionary);
    }

    /// <summary>让系统标题栏跟随主题（Windows 10 20H1 / Windows 11）。</summary>
    public static void ApplyTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var value = dark ? 1 : 0;

        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE，19 为旧版本号
        if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
