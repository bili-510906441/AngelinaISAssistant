using System;
using System.Runtime.InteropServices;
using System.Windows;

internal static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    private static void Pump(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static string LogText(System.Windows.Controls.TextBlock block) =>
        new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text.Trim();

    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new AngelinaISAssistant.App();
            app.InitializeComponent();

            AngelinaISAssistant.MainWindow.SuppressHotkeyChecks = true;

            var window = new AngelinaISAssistant.MainWindow();
            Console.WriteLine("SMOKE OK: window created, title = " + window.Title);

            var toggle = window.FindName("CaptureToggleButton") as System.Windows.Controls.Button;
            var status = window.FindName("CaptureStatus") as System.Windows.Controls.TextBlock;
            Console.WriteLine($"toggle={(toggle != null)}, toggleText={toggle?.Content}, status={status?.Text}");

            // 便携数据目录：全部跟随 release 包
            AngelinaISAssistant.AppSettings.Save();
            var settingsFile = System.IO.Path.Combine(AppContext.BaseDirectory, "config", "settings.json");
            Console.WriteLine($"settings: exists={System.IO.File.Exists(settingsFile)} dir={System.IO.Path.GetDirectoryName(settingsFile)}");
            Console.WriteLine($"logs dir={AngelinaISAssistant.Logger.LogDirectory}");
            Console.WriteLine($"reports dir={AngelinaISAssistant.CrashReporter.ReportDirectory}");

            // 崩溃报告链路
            AngelinaISAssistant.CrashReporter.OverrideRoot = System.IO.Path.Combine(
                AppContext.BaseDirectory, "smoke-reports-" + Guid.NewGuid().ToString("N"));

            var crashEx = new InvalidOperationException("冒烟测试异常");
            var report = AngelinaISAssistant.CrashReporter.Write(crashEx, "smoke");
            Console.WriteLine($"report written={System.IO.File.Exists(report)}, reason={AngelinaISAssistant.CrashReporter.TryReadReason(report)}");

            AngelinaISAssistant.CrashReporter.MarkPending(report);
            var pending = AngelinaISAssistant.CrashReporter.TryConsumePending();
            Console.WriteLine($"pending roundtrip={pending == report}");

            var crashDialog = new AngelinaISAssistant.CrashDialog("喜报：出错原因", "test reason", report);
            crashDialog.Close();
            Console.WriteLine("crash dialog ok");

            // 启动位置校验
            Console.WriteLine($"guard drive-root={AngelinaISAssistant.StartupLocationGuard.CheckDirectory(System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath()))}");
            Console.WriteLine($"guard temp={AngelinaISAssistant.StartupLocationGuard.CheckDirectory(System.IO.Path.GetTempPath())}");
            Console.WriteLine($"guard system={AngelinaISAssistant.StartupLocationGuard.CheckDirectory(Environment.SystemDirectory)}");
            Console.WriteLine($"guard appdir={AngelinaISAssistant.StartupLocationGuard.CheckDirectory(AppContext.BaseDirectory)}");
            Console.WriteLine($"report dir={AngelinaISAssistant.CrashReporter.ReportDirectory}");

            var debug = new AngelinaISAssistant.DebugWindow();
            Console.WriteLine("debug window ok, rows=" + ((System.Windows.Controls.Panel)debug.FindName("RowPanel")).Children.Count);
            debug.Close();

            // 四套语言字典
            foreach (var lang in new[] { "zh-CN", "zh-TW", "en-US", "ja-JP" })
            {
                AngelinaISAssistant.Loc.Apply(AngelinaISAssistant.Loc.Normalize(lang), force: true);
                Console.WriteLine($"lang={AngelinaISAssistant.Loc.Current} settings={AngelinaISAssistant.Loc.T("Str.TabSettings")} capture={AngelinaISAssistant.Loc.T("Str.Capture.Stop")}");
            }

            // 高级设置默认隐藏
            var perfSection = window.FindName("SectionPerformance") as System.Windows.FrameworkElement;
            var perfNav = window.FindName("SettingsNavPerformance") as System.Windows.FrameworkElement;
            var advButton = window.FindName("AdvancedToggleButton") as System.Windows.Controls.Button;
            Console.WriteLine($"advanced: section={perfSection?.Visibility} nav={perfNav?.Visibility} button={advButton?.Content}");

            // 高级设置开关：两次切换不应抛异常，滚动位置保持有效
            var toggleMethod = window.GetType().GetMethod("AdvancedToggle_Click",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            window.Show();
            var mainHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var mainAffinityOk = GetWindowDisplayAffinity(mainHandle, out var mainAffinity);
            Console.WriteLine($"main window display affinity: ok={mainAffinityOk} value=0x{mainAffinity:X}");

            if (!mainAffinityOk || mainAffinity != 0x11)
            {
                throw new InvalidOperationException($"main window should be excluded from capture, affinity=0x{mainAffinity:X}");
            }

            toggleMethod!.Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
            Console.WriteLine($"after show: {perfSection?.Visibility}");
            toggleMethod.Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
            Console.WriteLine($"after hide: {perfSection?.Visibility} offset={((System.Windows.Controls.ScrollViewer)window.FindName("SettingsScroll")).VerticalOffset:0}");
            window.Hide();

            // 逐个点击设置左侧导航，确认高亮不会被滚动联动改到别的栏目
            window.Show();
            var navPanel = (System.Windows.Controls.Panel)window.FindName("SettingsNav");
            foreach (System.Windows.Controls.RadioButton navItem in navPanel.Children)
            {
                navItem.IsChecked = true;
                Pump(420);
                var checkedTag = navPanel.Children.OfType<System.Windows.Controls.RadioButton>()
                    .FirstOrDefault(item => item.IsChecked == true)?.Tag;
                Console.WriteLine($"nav {navItem.Tag} -> {checkedTag}");
            }
            window.Hide();

            window.Show();
            Pump(300);

            var hotkeySection2 = window.FindName("SectionHotkeys") as System.Windows.FrameworkElement;
            var hotkeyBox = window.FindName("HotkeyBox") as System.Windows.Controls.TextBox;

            if (hotkeySection2?.Visibility != System.Windows.Visibility.Visible)
            {
                toggleMethod!.Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
                Pump(300);
            }

            hotkeyBox!.Focus();
            Pump(100);

            var source = System.Windows.PresentationSource.FromVisual(hotkeyBox)
                         ?? System.Windows.PresentationSource.FromVisual(window)!;

            void Press(System.Windows.Input.Key key)
            {
                var args = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    source,
                    0,
                    key)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                hotkeyBox.RaiseEvent(args);
            }

            var originalHotkey = AngelinaISAssistant.AppSettings.Current.HotkeyShowHide;

            Press(System.Windows.Input.Key.F5);
            Console.WriteLine($"hotkey set -> {hotkeyBox.Text}");
            Press(System.Windows.Input.Key.Back);
            Console.WriteLine($"hotkey clear -> {hotkeyBox.Text}");

            // 还原测试前的热键，避免影响真实配置
            AngelinaISAssistant.AppSettings.Current.HotkeyShowHide = originalHotkey;
            AngelinaISAssistant.AppSettings.Save();
            window.Hide();
            var hotkeySection = window.FindName("SectionHotkeys") as System.Windows.FrameworkElement;
            Console.WriteLine($"hotkey: box='{hotkeyBox?.Text}' section={hotkeySection?.Visibility} navCount={navPanel.Children.Count}");

            // 实时辅助：勾选项 → 设置 → 悬浮窗渲染 → 后端结果回调
            var relic = (System.Windows.Controls.CheckBox)window.FindName("LiveRelicRecord");
            var enemy = (System.Windows.Controls.CheckBox)window.FindName("LiveEnemyHealth");
            relic.IsChecked = true;
            enemy.IsChecked = true;
            Pump(100);
            Console.WriteLine($"live options: {AngelinaISAssistant.AppSettings.Current.LiveAssist}");
            relic.IsChecked = false;
            enemy.IsChecked = false;
            Pump(50);

            var fakeBackend = new FakeLiveBackend();
            var coordinator = new AngelinaISAssistant.Live.LiveAssistCoordinator(
                fakeBackend,
                () => AngelinaISAssistant.Live.LiveAssistOptions.BattleAssist,
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                TimeSpan.Zero);

            AngelinaISAssistant.Live.LiveAssistResult liveResult = null;
            coordinator.ResultsReady += result => liveResult = result;
            coordinator.OfferFrame(new byte[2 * 2 * 4], 2, 2);
            Pump(300);
            Console.WriteLine($"bridge items: {liveResult?.Items.Count}");
            coordinator.Dispose();

            var overlay = new AngelinaISAssistant.MaskOverlayWindow();
            var overlayCanvas = (System.Windows.Controls.Canvas)overlay.FindName("OverlayCanvas");
            var titleText = (System.Windows.Controls.TextBlock)overlay.FindName("TitleText");
            var logPanel = (System.Windows.Controls.StackPanel)overlay.FindName("LogPanel");

            // 后端每帧返回：追加式提示（Items）+ 按帧重绘的数值标注（Annotations）。
            // 遮罩按顺序追加提示、同一条不重复追加；标注每帧重画；第一行标题始终不变。
            var layerMessage = "已进入沉沦者的黑流树海第2层（甜美的伤口）";

            overlay.UpdateOverlay(new AngelinaISAssistant.Live.LiveAssistResult(
                new[]
                {
                    new AngelinaISAssistant.Live.OverlayItem(AngelinaISAssistant.Live.OverlayKind.Info, "识别到猎犬病原", 0, 0, 0, 0),
                    new AngelinaISAssistant.Live.OverlayItem(AngelinaISAssistant.Live.OverlayKind.Info, layerMessage, 0, 0, 0, 0)
                },
                new[]
                {
                    new AngelinaISAssistant.Live.OverlayAnnotation("2756", 900, 300, 24, 120, true),
                    new AngelinaISAssistant.Live.OverlayAnnotation("318", 900, 360, 24, 120, true)
                }));
            Console.WriteLine(
                $"overlay status: title='{titleText.Text}' lines={logPanel.Children.Count} canvas={overlayCanvas.Children.Count}");

            if (overlayCanvas.Children.Count != 2)
            {
                throw new InvalidOperationException($"overlay annotations were not drawn: {overlayCanvas.Children.Count}");
            }

            if (!titleText.Text.Contains("AngelinaISAssistant", StringComparison.Ordinal)
                || logPanel.Children.Count != 2)
            {
                throw new InvalidOperationException($"overlay status lines were not applied: title='{titleText.Text}' lines={logPanel.Children.Count}");
            }

            var firstLog = LogText((System.Windows.Controls.TextBlock)logPanel.Children[0]);
            var secondLog = LogText((System.Windows.Controls.TextBlock)logPanel.Children[1]);
            var firstAnnotation = (System.Windows.Controls.TextBlock)overlayCanvas.Children[0];
            Console.WriteLine($"  log0='{firstLog}'");
            Console.WriteLine($"  log1='{secondLog}'");
            Console.WriteLine($"  annotation0='{firstAnnotation.Text}' left={System.Windows.Controls.Canvas.GetLeft(firstAnnotation):F1} top={System.Windows.Controls.Canvas.GetTop(firstAnnotation):F1}");

            if (!firstLog.Contains("[INF]", StringComparison.Ordinal)
                || !firstLog.Contains("识别到猎犬病原", StringComparison.Ordinal)
                || !System.Text.RegularExpressions.Regex.IsMatch(firstLog, @"^\[\d\d:\d\d:\d\d\]"))
            {
                throw new InvalidOperationException($"stage name log line format unexpected: {firstLog}");
            }

            if (!secondLog.Contains("[INF]", StringComparison.Ordinal)
                || !secondLog.Contains("已进入沉沦者的黑流树海第2层", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"layer log line format unexpected: {secondLog}");
            }

            if (firstAnnotation.Text != "2756")
            {
                throw new InvalidOperationException($"overlay annotation text unexpected: {firstAnnotation.Text}");
            }

            // 同一批提示重复推送不重复追加（标注仍然每帧重画）
            overlay.UpdateOverlay(new AngelinaISAssistant.Live.LiveAssistResult(
                new[]
                {
                    new AngelinaISAssistant.Live.OverlayItem(AngelinaISAssistant.Live.OverlayKind.Info, "识别到猎犬病原", 0, 0, 0, 0),
                    new AngelinaISAssistant.Live.OverlayItem(AngelinaISAssistant.Live.OverlayKind.Info, layerMessage, 0, 0, 0, 0)
                },
                new[]
                {
                    new AngelinaISAssistant.Live.OverlayAnnotation("2756", 900, 300, 24, 120, true)
                }));

            if (logPanel.Children.Count != 2 || overlayCanvas.Children.Count != 1)
            {
                throw new InvalidOperationException("duplicate overlay lines should be ignored while annotations redraw");
            }

            // 新关卡名 + 旧层数提示：只追加新的那一条；标注切换成新敌人的数值
            overlay.UpdateOverlay(new AngelinaISAssistant.Live.LiveAssistResult(
                new[]
                {
                    new AngelinaISAssistant.Live.OverlayItem(AngelinaISAssistant.Live.OverlayKind.Info, "识别到开门请当心", 0, 0, 0, 0),
                    new AngelinaISAssistant.Live.OverlayItem(AngelinaISAssistant.Live.OverlayKind.Info, layerMessage, 0, 0, 0, 0)
                },
                new[]
                {
                    new AngelinaISAssistant.Live.OverlayAnnotation("68.9w", 400, 500, 22, 90, true),
                    new AngelinaISAssistant.Live.OverlayAnnotation("1.8e3", 800, 500, 22, 90, true),
                    new AngelinaISAssistant.Live.OverlayAnnotation("1000", 1200, 500, 22, 90, false)
                }));
            Console.WriteLine(
                $"overlay after append: title='{titleText.Text}' lines={logPanel.Children.Count} canvas={overlayCanvas.Children.Count}");

            if (!titleText.Text.Contains("AngelinaISAssistant", StringComparison.Ordinal)
                || logPanel.Children.Count != 3
                || overlayCanvas.Children.Count != 3)
            {
                throw new InvalidOperationException("overlay append failed");
            }

            // 没有标注的帧：标注清空、日志保留（离开敌方情报页后数字消失）
            overlay.UpdateOverlay(new AngelinaISAssistant.Live.LiveAssistResult(
                new[]
                {
                    new AngelinaISAssistant.Live.OverlayItem(AngelinaISAssistant.Live.OverlayKind.Info, layerMessage, 0, 0, 0, 0)
                }));

            if (overlayCanvas.Children.Count != 0 || logPanel.Children.Count != 3)
            {
                throw new InvalidOperationException("annotations should clear while log lines stay");
            }

            // result == null：清空全部（停止采集/切换设置）
            overlay.UpdateOverlay(null);
            Console.WriteLine($"overlay after clear: title='{titleText.Text}' lines={logPanel.Children.Count} canvas={overlayCanvas.Children.Count}");

            if (!titleText.Text.Contains("AngelinaISAssistant", StringComparison.Ordinal)
                || logPanel.Children.Count != 0
                || overlayCanvas.Children.Count != 0)
            {
                throw new InvalidOperationException("overlay clear failed");
            }

            // 清空后同一提示可以再次追加（新一轮采集）
            overlay.UpdateOverlay(new AngelinaISAssistant.Live.LiveAssistResult(
                new[]
                {
                    new AngelinaISAssistant.Live.OverlayItem(AngelinaISAssistant.Live.OverlayKind.Info, "识别到猎犬病原", 0, 0, 0, 0)
                }));

            if (logPanel.Children.Count != 1)
            {
                throw new InvalidOperationException("overlay should accept messages again after clear");
            }

            // 数值显示格式设置：选项存在、切换后写入配置
            var numberFormatCombo = (System.Windows.Controls.ComboBox)window.FindName("NumberFormatCombo");
            var numberFormatRow = (System.Windows.FrameworkElement)window.FindName("NumberFormatRow");
            var originalFormat = AngelinaISAssistant.AppSettings.Current.NumberFormat;

            numberFormatCombo.SelectedIndex = 1;
            Pump(80);

            Console.WriteLine(
                $"number format: items={numberFormatCombo.Items.Count} selected={numberFormatCombo.SelectedIndex} " +
                $"saved={AngelinaISAssistant.AppSettings.Current.NumberFormat} row={(numberFormatRow != null)}");

            if (numberFormatCombo.Items.Count != 3
                || AngelinaISAssistant.AppSettings.Current.NumberFormat != AngelinaISAssistant.NumberDisplayFormat.Scientific)
            {
                throw new InvalidOperationException("number format setting was not applied");
            }

            numberFormatCombo.SelectedIndex = 0;
            Pump(80);

            if (AngelinaISAssistant.AppSettings.Current.NumberFormat != AngelinaISAssistant.NumberDisplayFormat.Wan)
            {
                throw new InvalidOperationException("number format setting was not restored");
            }

            AngelinaISAssistant.AppSettings.Current.NumberFormat = originalFormat;
            AngelinaISAssistant.AppSettings.Save();

            // 遮罩窗口必须从屏幕采集中排除，否则 OCR 会识别到自己的旧结果
            overlay.Show();
            Pump(200);
            var overlayHandle = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            var affinityOk = GetWindowDisplayAffinity(overlayHandle, out var affinity);
            Console.WriteLine($"overlay display affinity: ok={affinityOk} value=0x{affinity:X}");

            if (!affinityOk || affinity != 0x11)
            {
                throw new InvalidOperationException($"overlay should be excluded from capture, affinity=0x{affinity:X}");
            }


            var overlayPanel = (System.Windows.Controls.StackPanel)overlay.FindName("OverlayPanel");
            for (var i = 0; i < 8; i++) overlay.AppendLog("INF", "line " + i);
            Console.WriteLine($"overlay: title='{titleText.Text}' align={overlayPanel.HorizontalAlignment}/{overlayPanel.VerticalAlignment} lines={logPanel.Children.Count}");

            if (logPanel.Children.Count != 6)
            {
                throw new InvalidOperationException($"overlay should keep at most 6 log lines, got {logPanel.Children.Count}");
            }

            overlay.Close();

            Console.WriteLine($"onnx: available={AngelinaISAssistant.OnnxRuntimeNative.IsAvailable} version='{AngelinaISAssistant.OnnxRuntimeNative.Version}'");

            var pages = (System.Windows.Controls.Panel)window.FindName("Pages");
            Console.WriteLine($"pages={pages.Children.Count}, first={pages.Children[0].GetType().Name}, visible={pages.Children[0].Visibility}");

            // 关闭主窗口时必须真正关掉遮罩窗口：只 Hide 的隐藏窗口会让进程残留（用户点叉号后不退出）
            var shutdownModeOk = System.Windows.Application.Current.ShutdownMode == System.Windows.ShutdownMode.OnMainWindowClose;
            var maskField = typeof(AngelinaISAssistant.MainWindow).GetField(
                "_maskOverlay",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var hiddenMask = new AngelinaISAssistant.MaskOverlayWindow();
            hiddenMask.Show();
            hiddenMask.Hide();
            maskField!.SetValue(window, hiddenMask);

            window.Close();

            var maskStillOpen = maskField.GetValue(window) is System.Windows.Window;
            Console.WriteLine(
                $"shutdown: modeOnMainWindowClose={shutdownModeOk} maskStillOpen={maskStillOpen} maskVisible={hiddenMask.IsVisible}");

            if (!shutdownModeOk || maskStillOpen || hiddenMask.IsVisible)
            {
                throw new InvalidOperationException("closing main window must close the mask overlay (otherwise the process stays alive)");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SMOKE FAIL: " + ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex.ToString());
            return 1;
        }
    }
}

internal sealed class FakeLiveBackend : AngelinaISAssistant.Live.ILiveAssistBackend
{
    public bool IsAvailable => true;
    public void Initialize() { }
    public void Dispose() { }

    public AngelinaISAssistant.Live.LiveAssistResult Process(
        AngelinaISAssistant.Live.CapturedFrame frame,
        AngelinaISAssistant.Live.LiveAssistOptions options)
    {
        return new AngelinaISAssistant.Live.LiveAssistResult(new[]
        {
            new AngelinaISAssistant.Live.OverlayItem(AngelinaISAssistant.Live.OverlayKind.Info, "fake", 10, 20, 30, 40)
        });
    }
}
