using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace AngelinaISAssistant;

/// <summary>崩溃提示窗口：标题为「悲报」，并引导用户前往问题反馈。</summary>
public partial class CrashDialog : Window
{
    private readonly string _reportPath;

    public CrashDialog(string headline, string reason, string reportPath)
    {
        InitializeComponent();

        _reportPath = reportPath;

        HeadlineText.Text = headline;
        ReasonText.Text = reason;
        GuideText.Text = Loc.T("Str.Crash.Guidance1") + Environment.NewLine + Loc.T("Str.Crash.Guidance2");
        ReportPathText.Text = string.IsNullOrEmpty(reportPath)
            ? Loc.T("Str.Crash.NoReport")
            : Loc.F("Str.Crash.ReportPath", reportPath);
        OpenFolderButton.IsEnabled = !string.IsNullOrEmpty(reportPath);

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this, ThemeManager.IsDark);
    }

    public static void ShowCrash(Window? owner, bool isPrevious, string reason, string reportPath)
    {
        var headline = Loc.T(isPrevious ? "Str.Crash.PrevHeadline" : "Str.Crash.LiveHeadline");
        var dialog = new CrashDialog(headline, reason, reportPath);

        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(_reportPath) && File.Exists(_reportPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_reportPath}\"") { UseShellExecute = true });
            }
            else
            {
                Directory.CreateDirectory(CrashReporter.ReportDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", CrashReporter.ReportDirectory) { UseShellExecute = true });
            }
        }
        catch
        {
            // 打开文件夹失败时忽略
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
