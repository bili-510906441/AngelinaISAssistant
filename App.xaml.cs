using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace AngelinaISAssistant;

public partial class App : Application
{
    /// <summary>把 onnxruntime 等原生库从 externals\onnxruntime 或开发目录加载。</summary>
    private static IntPtr ResolveNativeLibrary(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "externals", name + ".dll"),
            Path.Combine(AppContext.BaseDirectory, "externals", "onnxruntime", name + ".dll"),
            Path.Combine(AppContext.BaseDirectory, "onnxruntime", name + ".dll")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>PerMonitorV2 DPI 感知：保证采集/显示拿到的是物理像素（2560×1600 而不是 1706×1066）。</summary>
    private const int DpiAwarenessPerMonitorV2 = -4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    static App()
    {
        NativeLibrary.SetDllImportResolver(typeof(App).Assembly, ResolveNativeLibrary);

        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(DpiAwarenessPerMonitorV2));
        }
        catch
        {
            // 旧系统不支持时忽略，清单里的 dpiAwareness 仍会生效
        }

        // WinRT 相关程序集放在 externals 子目录，按需从这里加载
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var simpleName = name.Name;

            if (simpleName is null)
            {
                return null;
            }

            // 卫星资源程序集：externals\locales\<culture>\<name>.dll
            var culture = name.CultureName;

            if (!string.IsNullOrEmpty(culture))
            {
                var localesRoot = Path.Combine(AppContext.BaseDirectory, "externals", "locales");

                foreach (var candidate in CultureCandidates(culture))
                {
                    var localizedPath = Path.Combine(localesRoot, candidate, simpleName + ".dll");

                    if (File.Exists(localizedPath))
                    {
                        return AssemblyLoadContext.Default.LoadFromAssemblyPath(localizedPath);
                    }
                }
            }

            var path = Path.Combine(AppContext.BaseDirectory, "externals", simpleName + ".dll");

            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
    }

    /// <summary>从最具体到最宽泛的语言回退：zh-Hant → zh，pt-BR → pt。</summary>
    private static IEnumerable<string> CultureCandidates(string culture)
    {
        yield return culture;

        var dash = culture.IndexOf('-');

        if (dash > 0)
        {
            yield return culture[..dash];
        }
    }

    private bool _crashDialogShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 任何未处理异常：写报告；能继续运行就立刻提示，进程被终止则下次启动提示
        DispatcherUnhandledException += (_, args) =>
        {
            var path = CrashReporter.Write(args.Exception, "DispatcherUnhandledException");
            args.Handled = true;
            ShowCrash(false, Describe(args.Exception), path);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                var path = CrashReporter.Write(exception, "AppDomain.UnhandledException");
                CrashReporter.MarkPending(path);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashReporter.Write(args.Exception, "UnobservedTaskException");
            args.SetObserved();
        };

        // 同一目录只允许运行一个实例：第二个实例不创建主界面，只弹「悲报」后退出。
        if (!SingleInstance.TryAcquire())
        {
            ShowDuplicateInstanceNotice(e.Args);
            Shutdown();
            return;
        }

        Logger.Configure();

        if (AppSettings.Current.SoftwareRendering)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        // 依赖框架发布：缺 .NET 10 时 apphost 会先拦截；这里再兜底检查一次运行时
        if (!RuntimeRequirement.IsSatisfied())
        {
            RuntimeRequirement.ShowGuidance();
            Shutdown();
            return;
        }

        // 语言：优先用户设置，否则跟随系统，再归一到四种支持的语言
        var savedLanguage = AppSettings.Current.Language;
        var language = string.IsNullOrWhiteSpace(savedLanguage)
            ? Loc.Normalize(CultureInfo.CurrentUICulture.Name)
            : Loc.Normalize(savedLanguage);

        Loc.Apply(language, force: true);

        // 高危位置检查：盘符根目录 / 临时目录 / 系统或共享目录
        var locationIssue = StartupLocationGuard.Check();

        if (locationIssue is not null)
        {
            NoticeDialog.Show(
                null,
                Loc.T("Str.Location.Title"),
                Loc.F("Str.Location.Message", AppContext.BaseDirectory.TrimEnd('\\')),
                Loc.T("Str.Location.Detail"));

            Shutdown();
            return;
        }

        // 隐藏的崩溃自测入口：--crash-test（可恢复） / --crash-fatal（强制退出，下次启动提示）
        if (e.Args.Contains("--crash-test"))
        {
            Dispatcher.BeginInvoke(
                new Action(() => throw new InvalidOperationException("崩溃自测：这是一次可恢复的异常")),
                DispatcherPriority.ApplicationIdle);
        }
        else if (e.Args.Contains("--crash-fatal"))
        {
            var path = CrashReporter.Write(new InvalidOperationException("崩溃自测：进程被强制终止"), "crash-fatal");
            CrashReporter.MarkPending(path);
            Environment.FailFast("AngelinaISAssistant crash self-test");
        }

        base.OnStartup(e);

        // App.xaml 不再使用 StartupUri：先完成单实例检查等启动校验，再手动创建主窗口，
        // 这样重复启动的进程根本不会闪出主界面。
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        Dispatcher.BeginInvoke(new Action(ShowConfigNoticeIfAny), DispatcherPriority.ApplicationIdle);
        Dispatcher.BeginInvoke(new Action(ShowPendingCrashIfAny), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>同一目录已经有一个实例在运行：只提示并退出，不创建主界面。</summary>
    private void ShowDuplicateInstanceNotice(string[] args)
    {
        try
        {
            var dark = !Array.Exists(
                args,
                arg => string.Equals(arg, "--light", StringComparison.OrdinalIgnoreCase));

            ThemeManager.Apply(dark);
            Loc.Apply(Loc.Normalize(CultureInfo.CurrentUICulture.Name), force: true);

            ErrorDialog.ShowMessage(
                null,
                Loc.T("Str.Instance.Headline"),
                Loc.T("Str.Instance.Body"));
        }
        catch
        {
            // 弹窗失败时直接退出，避免卡住
        }
    }

    /// <summary>配置缺失或损坏时，明确告知用户已恢复默认配置，并提醒不要动 config 目录。</summary>
    private void ShowConfigNoticeIfAny()
    {
        var notice = AppSettings.StartupNotice;

        if (notice is null || notice.Issue == ConfigIssue.None)
        {
            return;
        }

        string headlineKey;
        string bodyKey;
        object[] args;

        switch (notice.Issue)
        {
            case ConfigIssue.Missing:
                headlineKey = "Str.Config.MissingHeadline";
                bodyKey = "Str.Config.MissingBody";
                args = Array.Empty<object>();
                break;

            case ConfigIssue.Corrupted:
                headlineKey = "Str.Config.CorruptedHeadline";
                bodyKey = "Str.Config.CorruptedBody";
                args = new object[] { notice.RecoveredFields, notice.BackupPath ?? "-" };
                break;

            default:
                headlineKey = "Str.Config.UnreadableHeadline";
                bodyKey = "Str.Config.UnreadableBody";
                args = new object[] { notice.Error ?? "unknown" };
                break;
        }

        try
        {
            ErrorDialog.ShowMessage(MainWindow, Loc.T(headlineKey), Loc.F(bodyKey, args));
        }
        catch
        {
            // 弹窗失败时不影响启动
        }
    }

    private void ShowPendingCrashIfAny()
    {
        var path = CrashReporter.TryConsumePending();

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var reason = CrashReporter.TryReadReason(path);
        ShowCrash(true, reason, path);
    }

    private void ShowCrash(bool previous, string reason, string reportPath)
    {
        if (_crashDialogShown)
        {
            return;
        }

        _crashDialogShown = true;

        try
        {
            CrashDialog.ShowCrash(MainWindow, previous, reason, reportPath);
        }
        catch
        {
            // 弹窗失败时不再递归
        }
        finally
        {
            _crashDialogShown = false;
        }
    }

    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message} (0x{exception.HResult:X8})";

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("application exiting");
        SingleInstance.Release();
        Logger.Shutdown();
        base.OnExit(e);
    }
}
