using System;
using System.Windows;

internal static class Program
{
    private static void Pump(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

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

            var overlay = new AngelinaISAssistant.MaskOverlayWindow();
            var overlayPanel = (System.Windows.Controls.StackPanel)overlay.FindName("OverlayPanel");
            var titleText = (System.Windows.Controls.TextBlock)overlay.FindName("TitleText");
            for (var i = 0; i < 8; i++) overlay.AppendLog("INF", "line " + i);
            var logPanel = (System.Windows.Controls.StackPanel)overlay.FindName("LogPanel");
            Console.WriteLine($"overlay: title='{titleText.Text}' align={overlayPanel.HorizontalAlignment}/{overlayPanel.VerticalAlignment} lines={logPanel.Children.Count}");
            overlay.Close();

            Console.WriteLine($"onnx: available={AngelinaISAssistant.OnnxRuntimeNative.IsAvailable} version='{AngelinaISAssistant.OnnxRuntimeNative.Version}'");

            var pages = (System.Windows.Controls.Panel)window.FindName("Pages");
            Console.WriteLine($"pages={pages.Children.Count}, first={pages.Children[0].GetType().Name}, visible={pages.Children[0].Visibility}");

            window.Close();
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
