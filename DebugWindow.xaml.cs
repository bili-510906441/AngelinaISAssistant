using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AngelinaISAssistant;

/// <summary>隐藏的 Debug 模式：逐个强行触发高危启动位置的拦截弹窗。</summary>
public partial class DebugWindow : Window
{
    private sealed record LocationCase(string NameKey, string Path);

    public DebugWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this, ThemeManager.IsDark);

        foreach (var item in Cases())
        {
            RowPanel.Children.Add(BuildRow(item));
        }
    }

    private static IEnumerable<LocationCase> Cases()
    {
        yield return new LocationCase("Str.Debug.Cat.Drive", @"D:\");
        yield return new LocationCase("Str.Debug.Cat.Temp", Path.GetTempPath());
        yield return new LocationCase("Str.Debug.Cat.Windows", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        yield return new LocationCase("Str.Debug.Cat.ProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        yield return new LocationCase("Str.Debug.Cat.ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        var publicRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments));
        if (!string.IsNullOrEmpty(publicRoot))
        {
            yield return new LocationCase("Str.Debug.Cat.Public", publicRoot);
        }
    }

    private UIElement BuildRow(LocationCase item)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var button = new Button
        {
            Content = Loc.T(item.NameKey),
            Style = (Style)FindResource("DebugButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 150
        };
        button.Click += (_, _) => Trigger(item);
        Grid.SetColumn(button, 0);
        grid.Children.Add(button);

        var path = new TextBlock
        {
            Text = $"{item.Path}    guard = {StartupLocationGuard.CheckDirectory(item.Path) ?? "(none)"}",
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("SubtleTextBrush")
        };
        Grid.SetColumn(path, 1);
        grid.Children.Add(path);

        return grid;
    }

    private void Trigger(LocationCase item)
    {
        NoticeDialog.Show(
            this,
            Loc.T("Str.Location.Title"),
            Loc.F("Str.Location.Message", item.Path),
            Loc.T("Str.Location.Detail"));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
