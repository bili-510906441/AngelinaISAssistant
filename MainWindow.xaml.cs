using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private const int LiveAssistPage = 0;

    private bool _ready;
    private int _previewFps = 10;

    private AngelinaISAssistant.Capture.ScreenCaptureService? _capture;
    private WriteableBitmap? _previewBitmap;
    private bool _captureErrorShown;

    private string _statusKey = "Str.Status.Idle";
    private object[] _statusArgs = Array.Empty<object>();

    private readonly List<SettingEntry> _settingsIndex = new();
    private readonly List<FrameworkElement> _sections = new();

    private DispatcherTimer? _searchTimer;
    private DispatcherTimer? _fpsTimer;
    private bool _syncingNav;
    private bool _suppressScrollSync;
    private readonly DispatcherTimer _scrollSyncTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };

    private const int HotkeyId = 0x4149;
    private const uint ModNoRepeat = 0x4000;
    private HwndSource? _hotkeySource;
    private readonly DispatcherTimer _hotkeyWatch = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _hotkeyChecking;

    /// <summary>测试用：跳过热键占用检测，避免自动化测试被模态弹窗卡住。</summary>
    internal static bool SuppressHotkeyChecks { get; set; }
    private bool _hotkeyConflictShown;
    private bool _showAdvanced;
    private MaskOverlayWindow? _maskOverlay;
    private bool _maskOverlayLogged;
    private bool _captureSettling;
    private readonly DispatcherTimer _captureSettleTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly List<(FrameworkElement Target, Adorner Adorner)> _searchHighlights = new();

    private readonly TranslateTransform _rainbowShift = new();
    private DoubleAnimation? _rainbowAnimation;

    private readonly DispatcherTimer _checkCooldown = new() { Interval = TimeSpan.FromSeconds(10) };

    /// <summary>离开查询界面 3 秒后释放已解码的图片缓存，避免长期占内存。</summary>

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
        LanguageCombo.SelectedIndex = Loc.Current switch
        {
            Loc.ChineseTraditional => 1,
            Loc.English => 2,
            Loc.Japanese => 3,
            _ => 0
        };

        UpdateModeSwitch.IsChecked = AppSettings.Current.UpdatePreference == UpdatePackagePreference.Full;

        Loc.LanguageChanged += (_, _) => RefreshLocalizedText();

        _sections.AddRange(new FrameworkElement[]
        {
            SectionConnection, SectionUpdate, SectionAppearance, SectionLanguage, SectionAchievements, SectionFeedback, SectionAbout, SectionPerformance, SectionHotkeys
        });


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


        SoftwareRenderSwitch.IsChecked = AppSettings.Current.SoftwareRendering;
        BuildCaptureBackendOptions();
        BuildGpuOptions();
        BuildSettingsIndex();

        _showAdvanced = AppSettings.Current.ShowAdvancedSettings;
        ApplyAdvancedVisibility();
        RefreshLocalizedText();

        // 默认停在「实时辅助」：Checked 事件在 XAML 初始化阶段触发时 Pages 还不存在，这里补一次
        if (Pages is not null && Pages.Children.Count > 0)
        {
            Pages.Children[0].Visibility = Visibility.Visible;
        }



        LogList.ItemsSource = Logger.Entries;
        Logger.Appended += OnLogAppended;

        LogStartup();

        if (!GraphicsCaptureSession.IsSupported())
        {
            SetStatus("Str.Status.Unsupported");
        }

        _ready = true;
        Loaded += (_, _) => ThemeManager.ApplyTitleBar(this, ThemeManager.IsDark);
        Closed += (_, _) =>
        {
            ReleaseCapture();
            CloseMaskOverlay();
        };

        // 程序主动滚动时短暂关闭“滚动联动左侧高亮”，避免小栏目被后续栏目顶掉选中
        _scrollSyncTimer.Tick += (_, _) =>
        {
            _scrollSyncTimer.Stop();
            _suppressScrollSync = false;
        };

        SourceInitialized += (_, _) => InitHotkey();
        Closed += (_, _) =>
        {
            _hotkeyWatch.Stop();
            UnregisterHotkey();
        };

        // 超时兜底：若后端一直没有回调，也要把按钮恢复可用，避免界面被永久锁住
        _captureSettleTimer.Tick += (_, _) =>
        {
            _captureSettleTimer.Stop();

            if (!_captureSettling)
            {
                return;
            }

            SetCaptureSettling(false);

            if (_capture is { IsRunning: true } && _frameWidth == 0)
            {
                SetStatus("Str.Status.Interrupted");
                Logger.Warn("capture settle timeout · no frame received");
            }
        };

        // 最小化时收起遮罩，恢复时按采集状态重新判断
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                CloseMaskOverlay();
            }
            else
            {
                UpdateMaskOverlay();
            }
        };

        if (!previewLatest)
        {
            _ = CheckForUpdatesAsync();
        }
    }

    /// <summary>重新套用所有由代码写入的本地化文本。</summary>
    private void RefreshLocalizedText()
    {
        UpdateGpuOptionTexts();
        UpdateCaptureBackendTexts();
        UpdateCaptureToggleText();
        var prefix = AlwaysOnTopSwitch.IsChecked == true ? Loc.T("Str.Title.AlwaysOnTop") + " " : string.Empty;
        Title = $"{prefix}{Loc.T("Str.App.Name")} - {AppInfo.Version} - {PlatformInfo.Current}";

        VersionText.Text = Loc.F("Str.About.Version", AppInfo.Version);
        PlatformText.Text = Loc.F("Str.About.Platform", PlatformInfo.Current);
        RuntimeText.Text = Loc.F("Str.About.Runtime", Environment.Version);

        CaptureStatus.Text = Loc.F(_statusKey, _statusArgs);

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
        Logger.Info("update check started");

        var result = await UpdateService.CheckAsync(AppInfo.Version);

        _updateState = result.State;
        _updateRemoteVersion = result.RemoteVersion;

        switch (result.State)
        {
            case UpdateCheckState.UpToDate:
                Logger.Success($"update check ---- up to date ({AppInfo.Version})");
                break;
            case UpdateCheckState.Outdated:
                Logger.Warn($"update check ---- outdated, remote {result.RemoteVersion}");
                break;
            default:
                Logger.Error("update check ---- failed");
                break;
        }

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

    /// <summary>选择优先使用增量包还是完整包，选择结果写入 settings.json。</summary>
    // ---- 性能设置：推理设备 ----

    private void BuildGpuOptions()
    {
        var devices = GpuCatalog.Devices;
        var items = new List<ComboBoxItem>
        {
            new() { Tag = -1, Content = Loc.T("Str.Performance.None") },
            new() { Tag = -2, Content = DefaultText(devices) }
        };

        for (var i = 0; i < devices.Count; i++)
        {
            items.Add(new ComboBoxItem { Tag = i, Content = devices[i] });
        }

        InferenceGpuCombo.ItemsSource = items;

        var saved = AppSettings.Current.InferenceGpu;
        InferenceGpuCombo.SelectedItem = items.FirstOrDefault(item => item.Tag is int value && value == saved) ?? items[1];
    }

    private static string DefaultText(IReadOnlyList<string> devices) =>
        devices.Count > 0 ? Loc.F("Str.Performance.DefaultWith", devices[0]) : Loc.T("Str.Performance.Default");

    private void UpdateGpuOptionTexts()
    {
        if (InferenceGpuCombo.ItemsSource is not List<ComboBoxItem> { Count: >= 2 } items)
        {
            return;
        }

        items[0].Content = Loc.T("Str.Performance.None");
        items[1].Content = DefaultText(GpuCatalog.Devices);
    }

    private bool _syncingSoftwareRender;

    private void SoftwareRender_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingSoftwareRender)
        {
            return;
        }

        var enabled = SoftwareRenderSwitch.IsChecked == true;

        if (enabled == AppSettings.Current.SoftwareRendering)
        {
            return;
        }

        var confirmed = ConfirmDialog.Ask(
            this,
            Loc.T("Str.SoftwareRender.ConfirmHeadline"),
            new[]
            {
                Loc.T("Str.SoftwareRender.ConfirmLine1"),
                Loc.T("Str.SoftwareRender.ConfirmLine2"),
                Loc.T("Str.SoftwareRender.ConfirmLine3")
            });

        if (!confirmed)
        {
            RevertSoftwareRenderSwitch();
            Logger.Info("software rendering change cancelled");
            return;
        }

        var previous = AppSettings.Current.SoftwareRendering;

        AppSettings.Current.SoftwareRendering = enabled;
        AppSettings.Save();

        if (!TryRestartApplication())
        {
            AppSettings.Current.SoftwareRendering = previous;
            AppSettings.Save();
            RevertSoftwareRenderSwitch();

            Logger.Error("software rendering change failed · cannot restart application");
            return;
        }

        Logger.Info($"software rendering {(enabled ? "enabled" : "disabled")} · restarting");
        Application.Current.Shutdown();
    }

    /// <summary>取消或失败时把勾选状态退回当前设置值，渲染方式保持不变。</summary>
    private void RevertSoftwareRenderSwitch()
    {
        _syncingSoftwareRender = true;
        SoftwareRenderSwitch.IsChecked = AppSettings.Current.SoftwareRendering;
        _syncingSoftwareRender = false;
    }

    private bool TryRestartApplication()
    {
        try
        {
            var path = Environment.ProcessPath ??
                       System.Reflection.Assembly.GetEntryAssembly()?.Location;

            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void InferenceGpu_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || InferenceGpuCombo.SelectedItem is not ComboBoxItem { Tag: int value } item)
        {
            return;
        }

        AppSettings.Current.InferenceGpu = value;
        AppSettings.Save();
        Logger.Info($"inference gpu set to {item.Content}");
    }

    // ---- 采集方式 ----

    private void BuildCaptureBackendOptions()
    {
        var items = new List<ComboBoxItem>
        {
            new() { Tag = CaptureBackend.Wgc, Content = Loc.T("Str.Capture.Wgc") },
            new() { Tag = CaptureBackend.Dxgi, Content = Loc.T("Str.Capture.Dxgi") },
            new() { Tag = CaptureBackend.BitBlt, Content = Loc.T("Str.Capture.BitBlt") }
        };

        CaptureBackendCombo.ItemsSource = items;

        var current = AppSettings.Current.CaptureBackend;
        CaptureBackendCombo.SelectedItem = items.FirstOrDefault(item => item.Tag is CaptureBackend backend && backend == current) ?? items[0];
    }

    private void UpdateCaptureBackendTexts()
    {
        if (CaptureBackendCombo.ItemsSource is not List<ComboBoxItem> { Count: >= 3 } items)
        {
            return;
        }

        items[0].Content = Loc.T("Str.Capture.Wgc");
        items[1].Content = Loc.T("Str.Capture.Dxgi");
        items[2].Content = Loc.T("Str.Capture.BitBlt");
    }

    private void CaptureBackend_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || CaptureBackendCombo.SelectedItem is not ComboBoxItem { Tag: CaptureBackend backend } item)
        {
            return;
        }

        if (AppSettings.Current.CaptureBackend == backend)
        {
            return;
        }

        // 切换采集方式：静默停止当前采集 → 释放旧后端依赖 → 新后端保持关闭，等用户手动开始
        StopCapture();
        ReleaseCapture();

        AppSettings.Current.CaptureBackend = backend;
        AppSettings.Save();

        SetStatus("Str.Status.Idle");
        UpdateCaptureToggleText();
        Logger.Info($"capture backend set to {backend} · capture released");
    }

    /// <summary>彻底释放采集服务与预览位图，下一次开始时按新后端重新创建。</summary>
    private void ReleaseCapture()
    {
        try
        {
            _capture?.Dispose();
        }
        catch
        {
            // 释放失败不影响后续重建
        }

        _capture = null;
        CloseMaskOverlay();
        _previewBitmap = null;
        PreviewImage.Source = null;
        FpsBadge.Visibility = Visibility.Collapsed;
        RealFpsText.Text = "0 fps";
        _framesSinceTick = 0;
        _frameWidth = 0;
        _frameHeight = 0;
    }

    // ---- 问题反馈 ----

    private const string IssuesUrl = "https://github.com/bili-510906441/AngelinaISAssistant/issues";

    private void FeedbackFaq_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("faq clicked · not implemented yet");
    }

    private void FeedbackIssues_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(IssuesUrl) { UseShellExecute = true });
            Logger.Success("opened github issues");
        }
        catch (Exception ex)
        {
            Logger.Error($"open github issues failed · {ex.Message}");
            ErrorDialog.Show(this, Loc.T("Str.Feedback.OpenFailed"), ex);
        }
    }

    // ---- 拖入更新包 ----


    private static readonly System.Text.RegularExpressions.Regex FolderPattern =
        new(@"angelina_isa_v(?<from>[\d.]+)_v(?<to>[\d.]+)\.aisapatch$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex FullPattern =
        new(@"angelina_isa_v(?<to>[\d.]+)_full\.zip$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
        {
            return;
        }

        var path = files[0];
        var name = System.IO.Path.GetFileName(path);
        var current = AppInfo.Version.TrimStart('v', 'V');

        var isZip = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var isFolderPackage = path.EndsWith(".aisapatch", StringComparison.OrdinalIgnoreCase);

        // 旧的单文件补丁（仅覆盖 dll）已不再支持
        if (path.EndsWith(".hdiff", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"rejected legacy single-file patch · {name}");
            ErrorDialog.Show(this, Loc.T("Str.Drop.LegacyDiff"), new InvalidOperationException(name));
            return;
        }

        if (!isZip && !isFolderPackage)
        {
            ErrorDialog.Show(this, Loc.T("Str.Drop.Invalid"), new InvalidOperationException(name));
            return;
        }

        var lines = new List<string> { Loc.F("Str.Drop.Package", name) };

        if (isFolderPackage)
        {
            var match = FolderPattern.Match(name);

            if (!match.Success)
            {
                ErrorDialog.Show(this, Loc.T("Str.Drop.Invalid"), new InvalidOperationException(name));
                return;
            }

            var from = match.Groups["from"].Value;
            var to = match.Groups["to"].Value;

            if (!string.Equals(from, current, StringComparison.OrdinalIgnoreCase))
            {
                ErrorDialog.Show(
                    this,
                    Loc.T("Str.Drop.Invalid"),
                    new InvalidOperationException(Loc.F("Str.Drop.DiffMismatch", from, current)));

                Logger.Warn($"rejected folder package · needs v{from}, current v{current}");
                return;
            }

            lines.Add(Loc.F("Str.Drop.Target", to, current));

            if (_updateRemoteVersion is { Length: > 0 } latest3 && CompareVersion(to, latest3) < 0)
            {
                lines.Add(Loc.F("Str.Drop.NewerAvailable", latest3, to));
            }
        }
        else
        {
            lines.Add(Loc.F("Str.Drop.FullPackage", current));

            var match = FullPattern.Match(name);
            if (match.Success && _updateRemoteVersion is { Length: > 0 } latest2 && CompareVersion(match.Groups["to"].Value, latest2) < 0)
            {
                lines.Add(Loc.F("Str.Drop.NewerAvailable", latest2, match.Groups["to"].Value));
            }
        }

        lines.Add(Loc.T("Str.Drop.Confirm"));

        if (!ConfirmDialog.Ask(this, Loc.T("Str.Drop.Headline"), lines))
        {
            Logger.Info($"update package dropped but cancelled · {name}");
            return;
        }

        try
        {
            UpdateProgressText.Visibility = Visibility.Visible;
            var result = await UpdateInstaller.ApplyLocalAsync(path, message => UpdateProgressText.Text = message);

            if (result.Status != UpdateApplyStatus.Success)
            {
                UpdateProgressText.Visibility = Visibility.Collapsed;
                ErrorDialog.Show(this, Loc.T("Str.Drop.Failed"), new InvalidOperationException(result.Message ?? name));
                return;
            }

            Logger.Success($"local update package applied · {name}");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, Loc.T("Str.Drop.Failed"), ex);
        }
    }

    private static int CompareVersion(string left, string right)
    {
        var a = left.TrimStart('v', 'V').Split('.');
        var b = right.TrimStart('v', 'V').Split('.');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length && int.TryParse(a[i], out var p1) ? p1 : 0;
            var y = i < b.Length && int.TryParse(b[i], out var p2) ? p2 : 0;

            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private void UpdatePreference_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        AppSettings.Current.UpdatePreference = UpdateModeSwitch.IsChecked == true
            ? UpdatePackagePreference.Full
            : UpdatePackagePreference.Incremental;

        AppSettings.Save();
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
        UpdateModeRow.IsEnabled = false;
        UpdateProgressText.Visibility = Visibility.Visible;
        UpdateProgressText.Text = Loc.T("Str.Update.Downloading");

        var result = await UpdateInstaller.RunAsync(
            _updateRemoteVersion ?? string.Empty,
            AppSettings.Current.UpdatePreference,
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
        UpdateModeRow.IsEnabled = true;
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

        private static void LogStartup()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? System.Reflection.Assembly.GetExecutingAssembly();
        var path = assembly.Location;

        var builtAt = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildTime")?.Value
            ?? (System.IO.File.Exists(path)
                ? System.IO.File.GetLastWriteTime(path).ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", System.Globalization.CultureInfo.InvariantCulture)
                : "unknown");

#if DEBUG
        const string env = "Debug";
#else
        const string env = "Release";
#endif

        Logger.Block(new[]
        {
            "AngelinaISAssistant GUI started",
            $"Version {AppInfo.Version}",
            $"Built at {builtAt}",
            $"AISA ENV: {env}",
            $"Command Line: {path}",
            $"User Dir {AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar)}"
        });
    }
    private void OnLogAppended()
    {
        if (Logger.Entries.Count == 0 || LogList is null || !LogList.IsVisible)
        {
            return;
        }

        LogList.ScrollIntoView(Logger.Entries[Logger.Entries.Count - 1]);
    }

    // ---- 顶部栏目：切到实时辅助就开采集，切走就停 ----
    private void CaptureToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_captureSettling)
        {
            return;
        }

        if (_capture is { IsRunning: true })
        {
            StopCapture();
        }
        else
        {
            StartCapture();
        }
    }

    /// <summary>采集处于“已请求、尚未生效”的过渡期时禁用按钮，避免连续开始/停止把状态搅乱。</summary>
    private void SetCaptureSettling(bool settling)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetCaptureSettling(settling)));
            return;
        }

        _captureSettling = settling;

        if (CaptureToggleButton is not null)
        {
            CaptureToggleButton.IsEnabled = !settling;
            CaptureToggleButton.Opacity = settling ? 0.6 : 1.0;
        }

        if (settling)
        {
            _captureSettleTimer.Stop();
            _captureSettleTimer.Start();
        }
        else
        {
            _captureSettleTimer.Stop();
        }
    }

    private void UpdateCaptureToggleText()
    {
        var running = _capture is { IsRunning: true };
        CaptureToggleButton.SetResourceReference(
            ContentControl.ContentProperty,
            running ? "Str.Capture.Stop" : "Str.Capture.Start");
    }

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

        ClearSearchHighlights();
        UpdateMaskOverlay();

        // 采集由「开始 / 停止」按钮控制，切走页面时只停止
        if (index != LiveAssistPage)
        {
            StopCapture();
        }

    }

    // ---- 设置页 ----

    private void InitHotkey()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (handle == IntPtr.Zero)
        {
            return;
        }

        _hotkeySource = System.Windows.Interop.HwndSource.FromHwnd(handle);
        _hotkeySource?.AddHook(HotkeyWndProc);

        ApplyHotkey(showFailure: false);

        // 只要不在编辑热键，就定期确认热键没有被其他程序抢走
        _hotkeyWatch.Tick += (_, _) => VerifyHotkey();
        _hotkeyWatch.Start();
    }

    /// <summary>
    /// 重新注册一次来确认热键是否仍归本程序所有：
    /// RegisterHotKey 失败说明被别的程序占了。
    /// </summary>
    private void VerifyHotkey()
    {
        if (SuppressHotkeyChecks || _hotkeyChecking || HotkeyBox?.IsKeyboardFocused == true)
        {
            return;
        }

        var text = (AppSettings.Current.HotkeyShowHide ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (handle == IntPtr.Zero || !TryParseHotkey(text, out var modifiers, out var key))
        {
            return;
        }

        _hotkeyChecking = true;

        try
        {
            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);

            UnregisterHotKey(handle, HotkeyId);

            if (RegisterHotKey(handle, HotkeyId, (uint)modifiers | ModNoRepeat, vk))
            {
                _hotkeyConflictShown = false;
                return;
            }

            Logger.Warn($"hotkey stolen · {text}");

            if (_hotkeyConflictShown)
            {
                return;
            }

            _hotkeyConflictShown = true;

            try
            {
                ErrorDialog.ShowMessage(
                    this,
                    Loc.T("Str.Hotkey.RegisterFailed"),
                    Loc.F("Str.Hotkey.ConflictDetail", text) + Environment.NewLine + Loc.T("Str.Hotkey.OwnerHint"));

                // 弹窗关闭后开始监听，用户按下热键时再抓占用进程
                HotkeyOwnerProbe.Start(
                    (uint)modifiers,
                    (uint)vk,
                    TimeSpan.FromSeconds(10),
                    pid => ReportHotkeyOwner(pid));
            }
            catch
            {
                // 弹窗失败时不重试，等下一轮检测
            }
        }
        finally
        {
            _hotkeyChecking = false;
        }
    }

    private void ReportHotkeyOwner(int pid)
    {
        try
        {
            if (pid == Environment.ProcessId)
            {
                ErrorDialog.ShowMessage(this, Loc.T("Str.Hotkey.OwnerTitle"), Loc.T("Str.Hotkey.OwnerUnknown"));
                return;
            }

            var process = System.Diagnostics.Process.GetProcessById(pid);
            var name = process.ProcessName;

            Logger.Warn($"hotkey owner detected · {name} ({pid})");

            ErrorDialog.ShowMessage(
                this,
                Loc.T("Str.Hotkey.OwnerTitle"),
                Loc.F("Str.Hotkey.OwnerFound", name, pid));
        }
        catch (Exception ex)
        {
            Logger.Warn($"hotkey owner report failed · {ex.Message}");
            ErrorDialog.ShowMessage(this, Loc.T("Str.Hotkey.OwnerTitle"), Loc.T("Str.Hotkey.OwnerUnknown"));
        }
    }

    private bool ApplyHotkey(bool showFailure = true)
    {
        UnregisterHotkey();

        var text = (AppSettings.Current.HotkeyShowHide ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            HotkeyBox.Text = Loc.T("Str.Hotkey.Empty");
            return true;
        }

        HotkeyBox.Text = text;

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (handle == IntPtr.Zero || !TryParseHotkey(text, out var modifiers, out var key))
        {
            return false;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        if (RegisterHotKey(handle, HotkeyId, (uint)modifiers | ModNoRepeat, vk))
        {
            return true;
        }

        Logger.Warn($"hotkey register failed · {text}");

        if (showFailure)
        {
            ErrorDialog.Show(this, Loc.T("Str.Hotkey.RegisterFailed"), new InvalidOperationException(text));
        }

        return false;
    }

    private void UnregisterHotkey()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (handle != IntPtr.Zero)
        {
            UnregisterHotKey(handle, HotkeyId);
        }
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            ToggleMainWindow();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ToggleMainWindow()
    {
        // 正在录入热键时忽略全局热键，避免窗口闪一下
        if (HotkeyBox?.IsKeyboardFocused == true)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            UpdateMaskOverlay();
            return;
        }

        if (IsVisible && IsActive)
        {
            CloseMaskOverlay();
            Hide();
        }
        else
        {
            Show();

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            UpdateMaskOverlay();
        }
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 录入期间先注销现有热键，避免用户按到当前组合时触发“显示/收起”造成闪屏
        UnregisterHotkey();
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 离开录入框时按当前设置恢复注册（录入成功时已经注册过，这里是兜底）
        ApplyHotkey(showFailure: false);
    }

    private void HotkeyBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HotkeyBox.Focus();
        Keyboard.Focus(HotkeyBox);
        e.Handled = true;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.Back or Key.Delete or Key.Escape)
        {
            AppSettings.Current.HotkeyShowHide = string.Empty;
            AppSettings.Save();
            ApplyHotkey(showFailure: false);
            Logger.Info("hotkey cleared");
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var parts = new List<string>();

        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");

        parts.Add(key.ToString());

        AppSettings.Current.HotkeyShowHide = string.Join("+", parts);
        AppSettings.Save();

        if (!ApplyHotkey(showFailure: false))
        {
            HotkeyBox.Text = Loc.T("Str.Hotkey.RegisterFailed");
        }

        Logger.Info($"hotkey set to {AppSettings.Current.HotkeyShowHide}");
    }

    private static bool TryParseHotkey(string text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;

        foreach (var token in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token)
            {
                case "Ctrl": modifiers |= ModifierKeys.Control; break;
                case "Shift": modifiers |= ModifierKeys.Shift; break;
                case "Alt": modifiers |= ModifierKeys.Alt; break;
                case "Win": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse<Key>(token, true, out key))
                    {
                        return false;
                    }
                    break;
            }
        }

        return key != Key.None;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void AdvancedToggle_Click(object sender, RoutedEventArgs e)
    {
        _showAdvanced = !_showAdvanced;

        AppSettings.Current.ShowAdvancedSettings = _showAdvanced;
        AppSettings.Save();

        ApplyAdvancedVisibility();

        Logger.Info($"advanced settings {(_showAdvanced ? "shown" : "hidden")}");
    }

    /// <summary>高级设置（性能设置）的显示状态：左侧导航、右侧分区、搜索索引一起切换。</summary>
    /// <summary>平滑滚动用的依赖属性，回调里真正改动 ScrollViewer 的偏移。</summary>
    public static readonly DependencyProperty AnimatedScrollOffsetProperty = DependencyProperty.Register(
        nameof(AnimatedScrollOffset),
        typeof(double),
        typeof(MainWindow),
        new PropertyMetadata(0d, (d, e) => ((MainWindow)d).SettingsScroll?.ScrollToVerticalOffset((double)e.NewValue)));

    public double AnimatedScrollOffset
    {
        get => (double)GetValue(AnimatedScrollOffsetProperty);
        set => SetValue(AnimatedScrollOffsetProperty, value);
    }

    private void SmoothScrollTo(double offset)
    {
        if (SettingsScroll is null)
        {
            return;
        }

        var target = Math.Clamp(offset, 0, Math.Max(0, SettingsScroll.ScrollableHeight));

        _suppressScrollSync = true;
        _scrollSyncTimer.Stop();
        _scrollSyncTimer.Start();

        BeginAnimation(AnimatedScrollOffsetProperty, null);
        AnimatedScrollOffset = SettingsScroll.VerticalOffset;

        BeginAnimation(AnimatedScrollOffsetProperty, new DoubleAnimation(
            SettingsScroll.VerticalOffset,
            target,
            TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void ApplyAdvancedVisibility()
    {
        var visibility = _showAdvanced ? Visibility.Visible : Visibility.Collapsed;

        // 隐藏前判断用户当前是否正停在高级设置里（选中导航或视口与性能分区相交）
        var wasInAdvanced = false;

        if (!_showAdvanced && SectionPerformance is not null && SectionPerformance.Visibility == Visibility.Visible)
        {
            var top = SectionTop(SectionPerformance);
            var viewTop = SettingsScroll.VerticalOffset;
            var viewBottom = viewTop + SettingsScroll.ViewportHeight;

            wasInAdvanced = SettingsNavPerformance?.IsChecked == true
                            || (top < viewBottom && top + SectionPerformance.ActualHeight > viewTop);
        }

        if (SettingsNavPerformance is not null)
        {
            SettingsNavPerformance.Visibility = visibility;
        }

        if (PerformanceDivider is not null)
        {
            PerformanceDivider.Visibility = visibility;
        }

        if (SectionPerformance is not null)
        {
            SectionPerformance.Visibility = visibility;
        }

        if (SettingsNavHotkeys is not null)
        {
            SettingsNavHotkeys.Visibility = visibility;
        }

        if (HotkeyDivider is not null)
        {
            HotkeyDivider.Visibility = visibility;
        }

        if (SectionHotkeys is not null)
        {
            SectionHotkeys.Visibility = visibility;
        }

        AdvancedToggleButton?.SetResourceReference(
            System.Windows.Controls.ContentControl.ContentProperty,
            _showAdvanced ? "Str.Advanced.Hide" : "Str.Advanced.Show");

        BuildSettingsIndex();

        if (wasInAdvanced)
        {
            // 右侧滚到最底（性能设置已在最后，隐藏后最底就是「关于我们」）
            var about = SettingsNav?.Children
                .OfType<System.Windows.Controls.RadioButton>()
                .FirstOrDefault(item => item.Tag as string == "6");

            if (about is not null)
            {
                _syncingNav = true;

                try
                {
                    about.IsChecked = true;
                }
                finally
                {
                    _syncingNav = false;
                }
            }

            // 等布局按新的可视状态更新后再滚，避免用到隐藏前的旧高度
            Dispatcher.BeginInvoke(
                new Action(() => SmoothScrollTo(SettingsScroll.ScrollableHeight)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

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

        ClearSearchHighlights();
        ScrollToSection(index);
    }

    private void ScrollToSection(int index)
    {
        if (index < 0 || index >= _sections.Count)
        {
            return;
        }

        var top = SectionTop(_sections[index]);

        _suppressScrollSync = true;
        _scrollSyncTimer.Stop();
        _scrollSyncTimer.Start();

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
        if (!_ready || _sections.Count == 0 || _suppressScrollSync)
        {
            return;
        }

        var offset = e.VerticalOffset + 16;
        var active = 0;

        for (var i = 0; i < _sections.Count; i++)
        {
            // 被隐藏的高级设置不参与“当前分组”判定
            if (_sections[i].Visibility != Visibility.Visible)
            {
                continue;
            }

            if (SectionTop(_sections[i]) <= offset)
            {
                active = i;
            }
        }

        // 滚到底时最后一段可能顶不到视口上方，直接归到最后一个可见分组
        if (e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 1)
        {
            for (var i = _sections.Count - 1; i >= 0; i--)
            {
                if (_sections[i].Visibility == Visibility.Visible)
                {
                    active = i;
                    break;
                }
            }
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

    private void HighlightSearchMatches(IReadOnlyList<SettingEntry> matches)
    {
        ClearSearchHighlights();

        foreach (var entry in matches)
        {
            var target = entry.Target;

            if (target is null || !target.IsVisible)
            {
                continue;
            }

            var layer = AdornerLayer.GetAdornerLayer(target);

            if (layer is null)
            {
                continue;
            }

            var adorner = new RainbowAdorner(target);
            layer.Add(adorner);
            _searchHighlights.Add((target, adorner));
        }
    }

    private void ClearSearchHighlights()
    {
        foreach (var (target, adorner) in _searchHighlights)
        {
            AdornerLayer.GetAdornerLayer(target)?.Remove(adorner);
        }

        _searchHighlights.Clear();
    }

    private void BuildSettingsIndex()
    {
        _settingsIndex.Clear();

        _settingsIndex.Add(new SettingEntry(
            new[] { "采集", "采集方式", "截图", "画面", "dxgi", "wgc", "bitblt", "capture", "backend" }, CaptureBackendRow));

        _settingsIndex.Add(new SettingEntry(
            new[] { "更新", "更新设置", "检查更新", "自动更新", "update", "updates" }, AutoUpdateRow));

        _settingsIndex.Add(new SettingEntry(
            new[] { "深色", "浅色", "深色模式", "主题", "外观", "dark", "light", "theme", "appearance" }, DarkModeRow));

        if (_showAdvanced)
        {
            _settingsIndex.Add(new SettingEntry(
                new[] { "性能", "性能设置", "gpu", "显卡", "加速", "推理", "performance", "acceleration" }, InferenceGpuRow));

            _settingsIndex.Add(new SettingEntry(
                new[] { "软件渲染", "渲染", "software", "render", "rendering", "cpu" }, SoftwareRenderRow));
        }

        _settingsIndex.Add(new SettingEntry(
            new[] { "语言", "语言设置", "中文", "英文", "日文", "繁体", "简体", "language", "english", "chinese", "japanese", "traditional", "simplified" }, LanguageRow));

        _settingsIndex.Add(new SettingEntry(
            new[] { "成就", "成就设置", "achievement", "achievements" }, SectionAchievements));

        _settingsIndex.Add(new SettingEntry(
            new[] { "反馈", "问题反馈", "常见问题", "faq", "feedback", "issue", "github", "建议" }, FeedbackRow));

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

        // 隐藏后门：精确输入 325799 进入 Debug 模式
        if (string.Equals(query, "325799", StringComparison.Ordinal))
        {
            SettingsSearch.Text = string.Empty;
            _searchTimer?.Stop();
            OpenDebugWindow();
            return;
        }

        _searchTimer?.Stop();

        if (query.Length == 0)
        {
            ClearSearchHighlights();
            return;
        }

        _searchTimer?.Start();
    }

    private void OpenDebugWindow()
    {
        Logger.Warn("debug mode opened");

        try
        {
            new DebugWindow { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            Logger.Error($"debug window failed · {ex.Message}");
        }
    }

    private void RunSettingsSearch()
    {
        var query = SettingsSearch.Text.Trim();

        // 拼音输入法打字时，单个 ASCII 字母只是组合中的中间状态，不触发跳转
        if (query.Length == 0 || (query.Length == 1 && query[0] < 128))
        {
            return;
        }

        var matches = _settingsIndex
            .Where(entry => entry.Keywords.Any(keyword => keyword.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matches.Count == 0)
        {
            ClearSearchHighlights();
            return;
        }

        HighlightSearchMatches(matches);

        NavigateToSetting(matches[0]);
    }

    /// <summary>滚动到目标设置项并闪一下边框；不抢焦点，避免打断输入法。</summary>
    private void NavigateToSetting(SettingEntry entry)
    {
        if (entry.Target is null)
        {
            return;
        }

        entry.Target.BringIntoView();
    }


    // ---- 语言 ----

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageCombo is null)
        {
            return;
        }

        var code = (LanguageCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string
                   ?? Loc.Chinese;

        Loc.Apply(Loc.Normalize(code));

        AppSettings.Current.Language = Loc.Current;
        AppSettings.Save();

        Logger.Info($"language set to {Loc.Current}");
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
        Logger.Info($"theme set to {(dark ? "dark" : "light")}");
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
        if (_captureSettling || _capture is { IsRunning: true })
        {
            return;
        }

        SetCaptureSettling(true);

        Logger.Info("screen capture starting");

        if (AppSettings.Current.CaptureBackend == AngelinaISAssistant.Capture.CaptureBackend.Wgc &&
            !GraphicsCaptureSession.IsSupported())
        {
            SetStatus("Str.Status.Unsupported");
            SetCaptureSettling(false);
            return;
        }

        _captureErrorShown = false;

        try
        {
    

            _capture ??= new AngelinaISAssistant.Capture.ScreenCaptureService(OnCaptureFrame, OnCaptureError);
            _capture.Start(AppSettings.Current.CaptureBackend, _previewFps);

            // 重置帧尺寸缓存，保证重新开始采集后的第一帧一定会刷新状态栏分辨率
            _frameWidth = 0;
            _frameHeight = 0;
            _framesSinceTick = 0;
            SetStatus("Str.Status.Starting");
            UpdateCaptureToggleText();
            Logger.Info($"capture backend = {AppSettings.Current.CaptureBackend}");
            UpdateMaskOverlay();
        }
        catch (Exception ex)
        {
            SetStatus("Str.Status.Failed");
            SetCaptureSettling(false);
            ErrorDialog.Show(this, Loc.T("Str.Error.CaptureStart"), ex);
        }
    }

    /// <summary>只有「实时辅助」页 + 正在采集时，才显示只读遮罩。</summary>
    private void UpdateMaskOverlay()
    {
        var onLivePage = Pages is not null
                         && Pages.Children.Count > LiveAssistPage
                         && Pages.Children[LiveAssistPage].Visibility == Visibility.Visible;

        var shouldShow = onLivePage && _capture is { IsRunning: true };

        if (!shouldShow)
        {
            CloseMaskOverlay();
            return;
        }

        try
        {
            _maskOverlay ??= new MaskOverlayWindow();

            if (!_maskOverlay.IsVisible)
            {
                _maskOverlay.Show();

                if (!_maskOverlayLogged)
                {
                    _maskOverlayLogged = true;
                    _maskOverlay.AppendLog("INF", Loc.T("Str.Mask.Shown"));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"mask overlay failed · {ex.Message}");
            CloseMaskOverlay();
        }
    }

    private void CloseMaskOverlay()
    {
        if (_maskOverlay is null)
        {
            return;
        }

        _maskOverlayLogged = false;

        try
        {
            _maskOverlay.Hide();
        }
        catch
        {
            // 关闭失败忽略
        }
    }

    private void StopCapture()
    {
        if (_capture is not { IsRunning: true })
        {
            SetCaptureSettling(false);
            return;
        }

        SetCaptureSettling(true);
        _capture.Stop();
        SetStatus("Str.Status.Stopped");
        UpdateCaptureToggleText();
        Logger.Info("screen capture stopped");

        FpsBadge.Visibility = Visibility.Collapsed;
        _framesSinceTick = 0;
        UpdateMaskOverlay();
        SetCaptureSettling(false);
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

    private int _frameWidth;
    private int _frameHeight;

    private void OnCaptureFrame(byte[] pixels, int width, int height)
    {
        // 采集回调发生在采集线程，任何 UI 操作都必须切回 Dispatcher
        Dispatcher.BeginInvoke(() =>
        {
            if (_captureSettling)
            {
                SetCaptureSettling(false);
            }

            try
            {
                // 尺寸变化、或状态还停在“正在启动”时，都要刷新成分辨率信息
                if (width != _frameWidth || height != _frameHeight || _statusKey != "Str.Status.Capturing")
                {
                    _frameWidth = width;
                    _frameHeight = height;
                    SetStatus("Str.Status.Capturing", width, height);
                }
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
            SetCaptureSettling(false);
            UpdateMaskOverlay();

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
