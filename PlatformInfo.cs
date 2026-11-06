using System;
using System.Runtime.InteropServices;

namespace AngelinaISAssistant;

internal static class PlatformInfo
{
    public static string Current { get; } = Detect();

    private static string Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            var version = Environment.OSVersion.Version;
            var name = version.Build >= 22000 ? "Windows 11" : "Windows 10";
            return $"{name} (build {version.Build})";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        return RuntimeInformation.OSDescription.Trim();
    }
}
