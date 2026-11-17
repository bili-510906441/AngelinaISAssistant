using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AngelinaISAssistant;

/// <summary>通过 EnumDisplayDevices 枚举显示适配器（显卡），无需额外依赖。</summary>
internal static class GpuCatalog
{
    private const int DisplayDeviceAttachedToDesktop = 0x1;

    public static IReadOnlyList<string> Devices { get; } = Detect();

    private static List<string> Detect()
    {
        var result = new List<string>();

        try
        {
            var device = new DisplayDevice();
            device.cb = Marshal.SizeOf<DisplayDevice>();

            for (uint index = 0; EnumDisplayDevices(null, index, ref device, 0); index++)
            {
                var name = device.DeviceString?.Trim();

                if (!string.IsNullOrEmpty(name) &&
                    (device.StateFlags & DisplayDeviceAttachedToDesktop) != 0 &&
                    !result.Contains(name))
                {
                    result.Add(name);
                }

                device.cb = Marshal.SizeOf<DisplayDevice>();
            }
        }
        catch
        {
            // 枚举失败时返回空列表，界面只显示「不使用 / 系统默认」
        }

        return result;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceIndex, ref DisplayDevice displayDevice, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}
