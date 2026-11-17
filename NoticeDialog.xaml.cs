using System.Windows;

namespace AngelinaISAssistant;

/// <summary>单按钮提示窗口（启动位置校验等场景）。</summary>
public partial class NoticeDialog : Window
{
    public NoticeDialog(string headline, string message, string detail = "")
    {
        InitializeComponent();

        HeadlineText.Text = headline;
        MessageText.Text = message;
        DetailText.Text = detail;
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this, ThemeManager.IsDark);
    }

    public static void Show(Window? owner, string headline, string message, string detail = "")
    {
        var dialog = new NoticeDialog(headline, message, detail);

        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }
}
