using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AngelinaISAssistant;

/// <summary>二次确认弹窗：取消 / 重启两个按钮，样式跟随深浅主题。</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string headline, IEnumerable<string> lines)
    {
        InitializeComponent();

        HeadlineText.Text = headline;

        foreach (var line in lines)
        {
            MessagePanel.Children.Add(new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                Margin = new Thickness(0, 0, 0, 2),
                Foreground = (Brush)FindResource("SubtleTextBrush")
            });
        }

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this, ThemeManager.IsDark);
    }

    public static bool Ask(Window? owner, string headline, IEnumerable<string> lines)
    {
        var dialog = new ConfirmDialog(headline, lines);

        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
