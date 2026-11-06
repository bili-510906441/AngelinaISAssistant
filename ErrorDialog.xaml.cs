using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AngelinaISAssistant;

public partial class ErrorDialog : Window
{
    private readonly string _report;

    public ErrorDialog(string headline, string reason, string details)
    {
        InitializeComponent();

        HeadlineText.Text = headline;
        ReasonText.Text = reason;
        DetailsText.Text = details;

        _report = string.Join(
            Environment.NewLine,
            headline,
            reason,
            string.Empty,
            details);

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this, ThemeManager.IsDark);
    }

    /// <summary>显示错误窗口。异常会先被剥到最内层再解释，避免包装层掩盖真实原因。</summary>
    public static void Show(Window? owner, string headline, Exception exception)
    {
        var root = exception;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        var dialog = new ErrorDialog(headline, Describe(root), BuildDetails(root, exception));

        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }

    private static string Describe(Exception root)
    {
        var code = root.HResult;

        var friendly = code switch
        {
            unchecked((int)0x80070005) => Loc.T("Str.Error.AccessDenied"),
            unchecked((int)0x80070057) => Loc.T("Str.Error.InvalidArgument"),
            unchecked((int)0x80004005) => Loc.T("Str.Error.Fail"),
            unchecked((int)0x80131509) => Loc.T("Str.Error.InvalidOperation"),
            _ => Loc.T("Str.Error.Unknown")
        };

        return $"{friendly}{Environment.NewLine}0x{code:X8} · {root.Message}";
    }

    private static string BuildDetails(Exception root, Exception original)
    {
        var builder = new StringBuilder();

        builder.AppendLine(Loc.F("Str.Error.Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        builder.AppendLine(Loc.F("Str.Error.Version", AppInfo.Version));
        builder.AppendLine(Loc.F("Str.Error.Platform", PlatformInfo.Current));
        builder.AppendLine(Loc.F("Str.Error.Runtime", $".NET {Environment.Version}"));
        builder.AppendLine(Loc.F("Str.Error.Exception", root.GetType().FullName ?? root.GetType().Name));
        builder.AppendLine(Loc.F("Str.Error.HResult", $"0x{root.HResult:X8}"));
        builder.AppendLine(Loc.F("Str.Error.Message", root.Message));
        builder.AppendLine();

        var chain = new List<string>();
        for (var current = original; current is not null; current = current.InnerException)
        {
            chain.Add($"{current.GetType().Name}: {current.Message} (0x{current.HResult:X8})");
        }

        if (chain.Count > 1)
        {
            builder.AppendLine(Loc.T("Str.Error.Chain"));
            foreach (var line in chain)
            {
                builder.AppendLine(line);
            }

            builder.AppendLine();
        }

        builder.AppendLine(Loc.T("Str.Error.Stack"));
        builder.AppendLine(original.ToString());

        return builder.ToString();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_report);
            CopyButton.Content = Loc.T("Str.Error.Copied");
        }
        catch
        {
            CopyButton.Content = Loc.T("Str.Error.CopyFailed");
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // 恢复成资源引用，语言切换后仍会跟着变
            CopyButton.SetResourceReference(ContentControl.ContentProperty, "Str.Error.Copy");
        };
        timer.Start();
    }
}
