using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using AngelinaISAssistant.Capture;
using Windows.Graphics.Capture;

namespace AngelinaISAssistant;

public partial class MainWindow : Window
{

    /// <summary>实时辅助在顶部栏目中的序号。</summary>
    private const int LiveAssistPage = 1;

    private bool _ready;
    private int _previewFps = 10;

    private ScreenCaptureController? _capture;
    private WriteableBitmap? _previewBitmap;
    private bool _captureErrorShown;

    private string _statusKey = "Str.Status.Idle";
    private object[] _statusArgs = Array.Empty<object>();

    private readonly List<SettingEntry> _settingsIndex = new();
    private readonly List<FrameworkElement> _sections = new();

    private DispatcherTimer? _searchTimer;
    private DispatcherTimer? _fpsTimer;
    private bool _syncingNav;

    private readonly TranslateTransform _rainbowShift = new();
    private DoubleAnimation? _rainbowAnimation;

    private readonly DispatcherTimer _checkCooldown = new() { Interval = TimeSpan.FromSeconds(10) };

    private UpdateCheckState _updateState = UpdateCheckState.Unknown;
    private string? _updateRemoteVersion;
    private bool _applying;

    private int _framesSinceTick;

    public MainWindow()
    {
        InitializeComponent();

        // 默认深色；需要浅色启动时传 --light
        var dark = !Environment.GetCommandLineArgs()
            .Any(arg => string.Equals(arg, "--light", StringComparison.OrdinalIgnoreCase));

        ThemeManager.Apply(dark);
        DarkModeSwitch.IsChecked = dark;

        FpsCombo.SelectedIndex = 2;      // 默认 10 fps
        LanguageCombo.SelectedIndex = 0; // 默认简体中文

        Loc.LanguageChanged += (_, _) => RefreshLocalizedText();

        _sections.AddRange(new FrameworkElement[]
        {
            SectionConnection, SectionUpdate, SectionAppearance, SectionLanguage, SectionAbout
        });

        BuildThemeCards();
        SetDifficulty(_difficulty);

        // 预览“已是最新”的普通样式：AngelinaISAssistant.exe --latest（同时跳过联网检查）
        var previewLatest = Environment.GetCommandLineArgs()
            .Any(arg => string.Equals(arg, "--latest", StringComparison.OrdinalIgnoreCase));

        ApplyBrandState();

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RunSettingsSearch();
        };

        // 每秒统计一次实际送达的帧数
        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fpsTimer.Tick += (_, _) => UpdateMeasuredFps();
        _fpsTimer.Start();

        _checkCooldown.Tick += (_, _) =>
        {
            _checkCooldown.Stop();
            CheckUpdateButton.IsEnabled = true;
        };

        BuildSettingsIndex();
        RefreshLocalizedText();

        if (!GraphicsCaptureSession.IsSupported())
        {
            SetStatus("Str.Status.Unsupported");
        }

        _ready = true;
        Loaded += (_, _) => ThemeManager.ApplyTitleBar(this, ThemeManager.IsDark);
        Closed += (_, _) => _capture?.Dispose();

        if (!previewLatest)
        {
            _ = CheckForUpdatesAsync();
        }
    }

    /// <summary>重新套用所有由代码写入的本地化文本。</summary>
    private void RefreshLocalizedText()
    {
        var prefix = AlwaysOnTopSwitch.IsChecked == true ? Loc.T("Str.Title.AlwaysOnTop") : string.Empty;
        Title = $"{prefix}{Loc.T("Str.App.Name")}-{AppInfo.Version}-{PlatformInfo.Current}";

        ThemeHint.Text = Loc.T(ThemeManager.IsDark ? "Str.Theme.Dark" : "Str.Theme.Light");

        VersionText.Text = Loc.F("Str.About.Version", AppInfo.Version);
        PlatformText.Text = Loc.F("Str.About.Platform", PlatformInfo.Current);
        RuntimeText.Text = Loc.F("Str.About.Runtime", Environment.Version);

        CaptureStatus.Text = Loc.F(_statusKey, _statusArgs);

        // 语言变化时重建层筛选按钮，并刷新列表里的类型/层级文案
        if (LayerFilters is not null && LayerFilters.Children.Count > 0)
        {
            BuildLayerFilters();
            ApplyFilter(_layerFilter);
        }

        RenderUpdateStatus();
    }

    // ---- 更新检查 ----

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateState == UpdateCheckState.Checking)
        {
            return;
        }

        // 10 秒内只允许手动检查一次，避免频繁请求被 B 站风控
        CheckUpdateButton.IsEnabled = false;
        _checkCooldown.Stop();
        _checkCooldown.Start();

        await CheckForUpdatesAsync();
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // 无法拉起浏览器时忽略
        }

        e.Handled = true;
    }

    private async Task CheckForUpdatesAsync()
    {
        _updateState = UpdateCheckState.Checking;
        RenderUpdateStatus();

        var result = await UpdateService.CheckAsync(AppInfo.Version);

        _updateState = result.State;
        _updateRemoteVersion = result.RemoteVersion;

        UpdateStatus.SetOutdated(result.State == UpdateCheckState.Outdated);
        ApplyBrandState();
        RenderUpdateStatus();
    }

    private void RenderUpdateStatus()
    {
        UpdateStatusText.Text = _updateState switch
        {
            UpdateCheckState.Checking => Loc.T("Str.Update.Checking"),
            UpdateCheckState.Outdated => Loc.F("Str.Update.Outdated", _updateRemoteVersion ?? string.Empty),
            UpdateCheckState.UpToDate => Loc.T("Str.Update.Latest"),
            UpdateCheckState.Failed => Loc.T("Str.Update.Failed"),
            _ => Loc.T("Str.Update.LastCheck")
        };

        UpdateFailedText.Visibility = _updateState == UpdateCheckState.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyUpdateButton.Visibility = _updateState == UpdateCheckState.Outdated && !_applying
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>下载并应用更新：优先增量包，其次整包，都没有就提示网络错误。</summary>
    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_applying || _updateState != UpdateCheckState.Outdated)
        {
            return;
        }

        _applying = true;
        ApplyUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateProgressText.Visibility = Visibility.Visible;
        UpdateProgressText.Text = Loc.T("Str.Update.Downloading");

        var result = await UpdateInstaller.RunAsync(
            _updateRemoteVersion ?? string.Empty,
            message => UpdateProgressText.Text = message);

        if (result.Status == UpdateApplyStatus.Success)
        {
            // 更新脚本会等本进程退出后替换文件并重新启动
            Application.Current.Shutdown();
            return;
        }

        _applying = false;
        ApplyUpdateButton.IsEnabled = true;
        CheckUpdateButton.IsEnabled = true;
        UpdateProgressText.Text = result.Message ?? Loc.T("Str.Update.Failed");
        ApplyUpdateButton.Visibility = Visibility.Visible;
    }

    // ---- 右上角品牌名：版本不是最新时彩虹色来回滚动 ----

    private void ApplyBrandState()
    {
        BrandHitArea.Cursor = UpdateStatus.IsOutdated ? Cursors.Hand : Cursors.Arrow;

        if (UpdateStatus.IsOutdated)
        {
            AngelinaBrand.Foreground = CreateRainbowBrush();

            _rainbowAnimation ??= new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.4))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            _rainbowShift.BeginAnimation(TranslateTransform.XProperty, _rainbowAnimation);
            return;
        }

        _rainbowShift.BeginAnimation(TranslateTransform.XProperty, null);
        AngelinaBrand.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
    }

    /// <summary>只有版本过旧（彩虹字体）时，点击品牌名才跳转到更新设置。</summary>
    private void Brand_Click(object sender, MouseButtonEventArgs e)
    {
        if (!UpdateStatus.IsOutdated)
        {
            return;
        }

        OpenUpdateSettings();
    }

    private void OpenUpdateSettings()
    {
        TabSettings.IsChecked = true;
        SettingsNavUpdate.IsChecked = true;

        // 等设置页完成布局后再滚动，否则此时 ScrollViewer 还没有可用的范围
        Dispatcher.BeginInvoke(
            () =>
            {
                UpdateLayout();
                ScrollToSection(1);
            },
            DispatcherPriority.Loaded);
    }

    private Brush CreateRainbowBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            SpreadMethod = GradientSpreadMethod.Repeat,
            RelativeTransform = _rainbowShift
        };

        string[] colors =
        {
            "#FF4D4D", "#FFA64D", "#FFE24D", "#4DFF88", "#4DD8FF", "#8A6DFF", "#FF4DD2"
        };

        for (var i = 0; i <= colors.Length; i++)
        {
            var color = (Color)ColorConverter.ConvertFromString(colors[i % colors.Length]);
            brush.GradientStops.Add(new GradientStop(color, (double)i / colors.Length));
        }

        return brush;
    }

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        CaptureStatus.Text = Loc.F(key, args);
    }

    // ---- 一键查询：集成战略主题 ----

    /// <summary>六个主题卡片按资源键生成，语言切换时文字会自动跟着变。</summary>
    private void BuildThemeCards()
    {
        ThemeList.Children.Clear();

        // 倒序排列：最新的一期在最前面
        for (var theme = 6; theme >= 1; theme--)
        {
            var name = new TextBlock
            {
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            name.SetResourceReference(TextBlock.TextProperty, $"Str.Theme.{theme}.Name");

            var alternate = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            alternate.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            alternate.SetResourceReference(TextBlock.TextProperty, $"Str.Theme.{theme}.Alt");

            var text = new StackPanel();
            text.Children.Add(name);
            text.Children.Add(alternate);

            // 右上角的年份，最新一期（2026）后面带一个对勾
            var yearValue = 2020 + theme;
            var year = new TextBlock
            {
                Text = theme == 6 ? $"{yearValue} ✓" : yearValue.ToString(),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10, 1, 0, 0)
            };
            year.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 0);
            Grid.SetColumn(year, 1);
            layout.Children.Add(text);
            layout.Children.Add(year);

            var card = new Button
            {
                Content = layout,
                Tag = theme.ToString(),
                Style = (Style)FindResource("ThemeCardStyle")
            };
            card.Click += Theme_Click;

            ThemeList.Children.Add(card);
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button card || card.Tag is not string tag || !int.TryParse(tag, out var theme))
        {
            return;
        }

        if (theme == 6)
        {
            ShowHub();
            return;
        }

        // 目前只支持黑流树海
        SelectedThemeName.SetResourceReference(TextBlock.TextProperty, "Str.Theme.Unsupported");
        SelectedThemeAlt.Visibility = Visibility.Collapsed;
        SelectedCaption.Visibility = Visibility.Collapsed;

        ThemePickerView.Visibility = Visibility.Collapsed;
        ThemeMapsView.Visibility = Visibility.Collapsed;
        StageDetailView.Visibility = Visibility.Collapsed;
        ThemeSelectedView.Visibility = Visibility.Visible;
    }

    private void ThemeBack_Click(object sender, RoutedEventArgs e) => ShowThemePicker();

    // ---- 黑流树海入口：难度 + 地图/收藏品/干员 ----

    private void ShowHub()
    {
        HubThemeName.SetResourceReference(TextBlock.TextProperty, "Str.Theme.6.Name");

        ThemePickerView.Visibility = Visibility.Collapsed;
        ThemeSelectedView.Visibility = Visibility.Collapsed;
        ThemeMapsView.Visibility = Visibility.Collapsed;
        StageDetailView.Visibility = Visibility.Collapsed;
        NoteView.Visibility = Visibility.Collapsed;
        ThemeFeatureView.Visibility = Visibility.Collapsed;
        ThemeHubView.Visibility = Visibility.Visible;
    }

    private void HubBack_Click(object sender, RoutedEventArgs e) => ShowThemePicker();

    private void MapsOption_Click(object sender, RoutedEventArgs e)
    {
        MapsThemeName.SetResourceReference(TextBlock.TextProperty, "Str.Theme.6.Name");

        if (LayerFilters.Children.Count == 0)
        {
            BuildLayerFilters();
        }

        if (StageList.ItemsSource is null)
        {
            ApplyFilter(_layerFilter);
        }

        ThemeHubView.Visibility = Visibility.Collapsed;
        ThemeFeatureView.Visibility = Visibility.Collapsed;
        ThemeMapsView.Visibility = Visibility.Visible;
    }

    private void CollectiblesOption_Click(object sender, RoutedEventArgs e) => ShowFeature("Str.Hub.Collectibles");

    private void OperatorsOption_Click(object sender, RoutedEventArgs e) => ShowFeature("Str.Hub.Operators");

    private void ShowFeature(string titleKey)
    {
        FeatureTitle.SetResourceReference(TextBlock.TextProperty, titleKey);
        ThemeHubView.Visibility = Visibility.Collapsed;
        ThemeFeatureView.Visibility = Visibility.Visible;
    }

    private void FeatureBack_Click(object sender, RoutedEventArgs e)
    {
        ThemeFeatureView.Visibility = Visibility.Collapsed;
        ThemeHubView.Visibility = Visibility.Visible;
    }

    private void MapsBack_Click(object sender, RoutedEventArgs e)
    {
        ThemeMapsView.Visibility = Visibility.Collapsed;
        ThemeHubView.Visibility = Visibility.Visible;
    }

    private void DetailBack_Click(object sender, RoutedEventArgs e)
    {
        StageDetailView.Visibility = Visibility.Collapsed;
        ThemeMapsView.Visibility = Visibility.Visible;
    }

    private void ShowThemePicker()
    {
        StageDetailView.Visibility = Visibility.Collapsed;
        NoteView.Visibility = Visibility.Collapsed;
        ThemeMapsView.Visibility = Visibility.Collapsed;
        ThemeHubView.Visibility = Visibility.Collapsed;
        ThemeFeatureView.Visibility = Visibility.Collapsed;
        ThemeSelectedView.Visibility = Visibility.Collapsed;
        ThemePickerView.Visibility = Visibility.Visible;
    }

    // ---- 黑流树海关卡列表 ----

    private void DifficultyMinus_Click(object sender, RoutedEventArgs e) => SetDifficulty(_difficulty - 1);

    private void DifficultyPlus_Click(object sender, RoutedEventArgs e) => SetDifficulty(_difficulty + 1);

    /// <summary>难度只在 0–16 之间选择，暂时不参与任何计算。</summary>
    private void SetDifficulty(int value)
    {
        _difficulty = Math.Clamp(value, 0, 16);
        DifficultyValue.Text = _difficulty.ToString();
        DifficultyMinus.IsEnabled = _difficulty > 0;
        DifficultyPlus.IsEnabled = _difficulty < 16;
    }

    private int _layerFilter = -1;
    private int _difficulty = 16;
    private bool _buildingFilters;
    private StageEntry? _detailStage;

    /// <summary>按层生成筛选按钮：全部 / 1层 ... 6层。</summary>
    private void BuildLayerFilters()
    {
        _buildingFilters = true;
        LayerFilters.Children.Clear();

        LayerFilters.Children.Add(CreateFilterChip(Loc.T("Str.Filter.All"), "all", _layerFilter < 0));

        for (var layer = 1; layer <= 6; layer++)
        {
            LayerFilters.Children.Add(
                CreateFilterChip(Loc.F("Str.Layer.N", layer), layer.ToString(), _layerFilter == layer));
        }

        _buildingFilters = false;
    }

    private RadioButton CreateFilterChip(string text, string tag, bool isChecked)
    {
        var chip = new RadioButton
        {
            Content = text,
            Tag = tag,
            IsChecked = isChecked,
            Style = (Style)FindResource("ChipStyle")
        };

        chip.Checked += LayerFilter_Checked;
        return chip;
    }

    private void LayerFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (_buildingFilters || !_ready || sender is not RadioButton button || button.IsChecked != true)
        {
            return;
        }

        _layerFilter = button.Tag is string tag && int.TryParse(tag, out var layer) ? layer : -1;
        ApplyFilter(_layerFilter);
    }

    /// <summary>
    /// 按层筛选。层号为 0 表示「任意层」，这类关卡在每一层都可能出现，所以每个层都会带上它们；
    /// 选中具体层时，先列这一层专属的关卡，任意层的排在后面。
    /// </summary>
    private void ApplyFilter(int layer)
    {
        var stages = StageCatalog.Ro6;

        StageList.ItemsSource = layer < 0
            ? stages.ToList()
            : stages
                .Where(stage => stage.Layers.Contains(layer) || stage.Layers.Contains(0))
                .OrderBy(stage => stage.Layers.Contains(0) ? 1 : 0)
                .ToList();
    }

    private void StageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StageList.SelectedItem is not StageEntry stage)
        {
            return;
        }

        StageList.SelectedItem = null; // 允许再次点同一项
        ShowStageDetail(stage);
    }

    private void ShowStageDetail(StageEntry stage)
    {
        _detailStage = stage;

        DetailName.Text = stage.Name;
        DetailDescription.Text = stage.DisplayDescription;
        DetailInfo.Text = string.Join(" · ", new[] { stage.SummaryText, stage.DlcText }.Where(s => s.Length > 0));

        // 只有打开详情才解码图片
        DetailImage.Source = stage.Image;

        ThemeMapsView.Visibility = Visibility.Collapsed;
        NoteView.Visibility = Visibility.Collapsed;
        StageDetailView.Visibility = Visibility.Visible;
    }

    // ---- 错题本 ----

    private void Note_Click(object sender, RoutedEventArgs e)
    {
        if (_detailStage is null)
        {
            return;
        }

        NoteTitle.Text = _detailStage.Name;
        NoteBox.Text = StageNotes.Get(_detailStage.Id);
        NoteStatus.Text = string.Empty;

        StageDetailView.Visibility = Visibility.Collapsed;
        NoteView.Visibility = Visibility.Visible;
        NoteBox.Focus();
    }

    private void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if (_detailStage is null)
        {
            return;
        }

        StageNotes.Set(_detailStage.Id, NoteBox.Text);
        NoteStatus.Text = Loc.T("Str.Note.Saved");
    }

    private void NoteBack_Click(object sender, RoutedEventArgs e)
    {
        if (_detailStage is not null)
        {
            // 返回时自动保存一次，避免忘记点保存
            StageNotes.Set(_detailStage.Id, NoteBox.Text);
        }

        NoteView.Visibility = Visibility.Collapsed;
        StageDetailView.Visibility = Visibility.Visible;
    }

    // ---- 顶部栏目：切到实时辅助就开采集，切走就停 ----
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Pages is null || sender is not RadioButton button || button.IsChecked != true)
        {
            return;
        }

        if (button.Tag is not string tag || !int.TryParse(tag, out var index))
        {
            return;
        }

        for (var i = 0; i < Pages.Children.Count; i++)
        {
            Pages.Children[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        if (index == LiveAssistPage)
        {
            StartCapture();
        }
        else
        {
            StopCapture();
        }
    }

    // ---- 设置页 ----

    private void SettingsNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingNav || sender is not RadioButton button || button.IsChecked != true)
        {
            return;
        }

        if (button.Tag is not string tag || !int.TryParse(tag, out var index))
        {
            return;
        }

        ScrollToSection(index);
    }

    private void ScrollToSection(int index)
    {
        if (index < 0 || index >= _sections.Count)
        {
            return;
        }

        var top = SectionTop(_sections[index]);
        SettingsScroll.ScrollToVerticalOffset(Math.Max(0, top - 4));
    }

    private double SectionTop(FrameworkElement section)
    {
        try
        {
            return section.TranslatePoint(new Point(0, 0), SettingsSections).Y;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>右边整列滚动时，左侧索引跟着高亮当前所在的设置分组。</summary>
    private void SettingsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_ready || _sections.Count == 0)
        {
            return;
        }

        var offset = e.VerticalOffset + 16;
        var active = 0;

        for (var i = 0; i < _sections.Count; i++)
        {
            if (SectionTop(_sections[i]) <= offset)
            {
                active = i;
            }
        }

        // 滚到底时最后一段可能顶不到视口上方，直接归到最后一组
        if (e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 1)
        {
            active = _sections.Count - 1;
        }

        SelectNav(active);
    }

    private void SelectNav(int index)
    {
        if (index < 0 || index >= SettingsNav.Children.Count)
        {
            return;
        }

        if (SettingsNav.Children[index] is not RadioButton radio || radio.IsChecked == true)
        {
            return;
        }

        _syncingNav = true;
        radio.IsChecked = true;
        _syncingNav = false;
    }

    private void BuildSettingsIndex()
    {
        _settingsIndex.Clear();

        _settingsIndex.Add(new SettingEntry(
            new[] { "服务地址", "地址", "服务器", "server", "address", "url" }, ServerUrlBox));

        _settingsIndex.Add(new SettingEntry(
            new[] { "端口", "port" }, PortBox));

        _settingsIndex.Add(new SettingEntry(
            new[] { "连接", "连接设置", "重连", "自动重连", "connection", "reconnect" }, AutoReconnectRow));

        _settingsIndex.Add(new SettingEntry(
            new[] { "更新", "更新设置", "检查更新", "自动更新", "update", "updates" }, AutoUpdateRow));

        _settingsIndex.Add(new SettingEntry(
            new[] { "深色", "浅色", "深色模式", "主题", "外观", "dark", "light", "theme", "appearance" }, DarkModeRow));

        _settingsIndex.Add(new SettingEntry(
            new[] { "语言", "语言设置", "中文", "英文", "language", "english", "chinese" }, LanguageRow));

        _settingsIndex.Add(new SettingEntry(
            new[] { "关于", "版本", "平台", "运行时", "about", "version", "platform", "runtime" }, AboutRow));
    }

    /// <summary>设置搜索：停止输入一小会儿后再匹配，避免输入法组合过程中反复跳转。</summary>
    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is null || SettingsSearch is null)
        {
            return;
        }

        var query = SettingsSearch.Text.Trim();
        SearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        _searchTimer?.Stop();

        if (query.Length == 0)
        {
            return;
        }

        _searchTimer?.Start();
    }

    private void RunSettingsSearch()
    {
        var query = SettingsSearch.Text.Trim();

        // 拼音输入法打字时，单个 ASCII 字母只是组合中的中间状态，不触发跳转
        if (query.Length == 0 || (query.Length == 1 && query[0] < 128))
        {
            return;
        }

        var match = _settingsIndex.FirstOrDefault(
            entry => entry.Keywords.Any(keyword => keyword.Contains(query, StringComparison.OrdinalIgnoreCase)));

        if (match is not null)
        {
            NavigateToSetting(match);
        }
    }

    /// <summary>滚动到目标设置项并闪一下边框；不抢焦点，避免打断输入法。</summary>
    private void NavigateToSetting(SettingEntry entry)
    {
        if (entry.Target is null)
        {
            return;
        }

        entry.Target.BringIntoView();

        if (entry.Target is Border border)
        {
            FlashBorder(border);
        }
    }

    private void FlashBorder(Border border)
    {
        border.BorderBrush = (Brush)FindResource("AccentBrush");
        border.BorderThickness = new Thickness(2);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            border.BorderThickness = new Thickness(1);
        };
        timer.Start();
    }

    // ---- 语言 ----

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageCombo is null)
        {
            return;
        }

        Loc.Apply(LanguageCombo.SelectedIndex == 1 ? Loc.English : Loc.Chinese);
    }

    // ---- 外观 ----

    private void DarkMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var dark = DarkModeSwitch.IsChecked == true;

        ThemeManager.Apply(dark);
        ThemeManager.ApplyTitleBar(this, dark);
        RefreshLocalizedText();
    }

    private void AlwaysOnTop_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        Topmost = AlwaysOnTopSwitch.IsChecked == true;
        RefreshLocalizedText();
    }

    // ---- 实时辅助：主显示器全屏采集 ----

    private void StartCapture()
    {
        if (_capture is { IsRunning: true })
        {
            return;
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            SetStatus("Str.Status.Unsupported");
            return;
        }

        _captureErrorShown = false;

        try
        {
            var item = ScreenCaptureController.CreatePrimaryMonitorItem();

            _capture ??= new ScreenCaptureController(OnCaptureFrame, OnCaptureError);
            _capture.Start(item, _previewFps);

            _framesSinceTick = 0;
            SetStatus("Str.Status.Capturing", item.Size.Width, item.Size.Height);
        }
        catch (Exception ex)
        {
            SetStatus("Str.Status.Failed");
            ErrorDialog.Show(this, Loc.T("Str.Error.CaptureStart"), ex);
        }
    }

    private void StopCapture()
    {
        if (_capture is not { IsRunning: true })
        {
            return;
        }

        _capture.Stop();
        SetStatus("Str.Status.Stopped");

        FpsBadge.Visibility = Visibility.Collapsed;
        _framesSinceTick = 0;
    }

    /// <summary>每秒把这一秒实际渲染的帧数写进预览右上角。</summary>
    private void UpdateMeasuredFps()
    {
        if (_capture is not { IsRunning: true })
        {
            FpsBadge.Visibility = Visibility.Collapsed;
            _framesSinceTick = 0;
            return;
        }

        RealFpsText.Text = $"{_framesSinceTick} fps";
        FpsBadge.Visibility = Visibility.Visible;
        _framesSinceTick = 0;
    }

    private void Fps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo ||
            combo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag ||
            !int.TryParse(tag, out var fps))
        {
            return;
        }

        _previewFps = fps;
        _capture?.SetFps(fps);
    }

    private void OnCaptureFrame(byte[] pixels, int width, int height)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_previewBitmap is null ||
                    _previewBitmap.PixelWidth != width ||
                    _previewBitmap.PixelHeight != height)
                {
                    _previewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    PreviewImage.Source = _previewBitmap;
                }

                var rowBytes = width * 4;
                if (pixels.Length < rowBytes * height)
                {
                    return;
                }

                _previewBitmap.Lock();

                var destination = _previewBitmap.BackBuffer;
                var stride = _previewBitmap.BackBufferStride;

                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(destination, y * stride), rowBytes);
                }

                _previewBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _previewBitmap.Unlock();

                _framesSinceTick++;
            }
            catch
            {
                // 绘制失败时丢弃这一帧
            }
        });
    }

    private void OnCaptureError(Exception ex)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SetStatus("Str.Status.Interrupted");

            if (_captureErrorShown)
            {
                return;
            }

            _captureErrorShown = true;
            ErrorDialog.Show(this, Loc.T("Str.Error.CaptureInterrupted"), ex);
        });
    }

    private sealed record SettingEntry(string[] Keywords, FrameworkElement? Target);
}
