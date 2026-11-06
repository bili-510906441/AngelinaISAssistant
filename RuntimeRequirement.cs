using System;
using System.Diagnostics;
using System.Windows;

namespace AngelinaISAssistant;

/// <summary>
/// .NET 运行时检查。程序本身是依赖框架发布的，缺少运行时时会由 apphost 先弹窗提示；
/// 这里再兜底检查一次实际运行时的主版本号，避免在更低的运行时上出现难以理解的异常。
/// </summary>
internal static class RuntimeRequirement
{
    public const int RequiredMajor = 10;

    public const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";

    public static bool IsSatisfied() => Environment.Version.Major >= RequiredMajor;

    public static void ShowGuidance()
    {
        var message = string.Join(
            Environment.NewLine,
            Loc.T("Str.Runtime.Missing"),
            string.Empty,
            DownloadUrl,
            string.Empty,
            Loc.T("Str.Runtime.OpenPage"));

        var result = MessageBox.Show(
            message,
            Loc.T("Str.Runtime.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            }
            catch
            {
                // 打不开浏览器时至少把链接留在提示里
            }
        }
    }
}
