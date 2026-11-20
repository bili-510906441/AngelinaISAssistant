using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AngelinaISAssistant;

internal enum LogLevel
{
    Info,
    Success,
    Warning,
    Error
}

internal sealed class LogItem
{
    public DateTime Time { get; init; }

    public LogLevel Level { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>[2026-09-09 18:33:11.927][INF]</summary>
    public string Stamp => $"[{Time:yyyy-MM-dd HH:mm:ss.fff}][{Tag}]";

    /// <summary>整行输出：[时间][级别] 内容</summary>
    public string Line => $"{Stamp} {Title}";

    public string Tag => Level switch
    {
        LogLevel.Success => "SUC",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "INF"
    };
}

/// <summary>
/// 日志门面：底层用 Serilog 写文件（按天滚动），同时通过 UiLogSink 同步到日志页。
/// </summary>
internal static class Logger
{
    private const int MaxEntries = 1000;

    private const string UiLevelProperty = "UiLevel";

    private const string SuccessPrefix = "[SUC] ";

    public static ObservableCollection<LogItem> Entries { get; } = new();

    public static event Action? Appended;

    public static string LogDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "config", "logs");

    private static bool _configured;

    public static void Configure()
    {
        if (_configured)
        {
            return;
        }

        _configured = true;

        Directory.CreateDirectory(LogDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(LogDirectory, "AngelinaISAssistant-.log"),
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}][{Level:u3}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: false)
            .WriteTo.Sink(new UiLogSink())
            .CreateLogger();
    }

    public static void Shutdown() => Log.CloseAndFlush();

    public static void Info(string title) => Log.Information("{Message:l}", title);

    public static void Success(string title) =>
        Log.ForContext(UiLevelProperty, "SUC")
            .Information("{Prefix:l}{Message:l}", SuccessPrefix, title);

    public static void Warn(string title) => Log.Warning("{Message:l}", title);

    public static void Error(string title) => Log.Error("{Message:l}", title);

    /// <summary>多行日志：每一行都单独输出一条。</summary>
    public static void Block(IEnumerable<string> lines, LogLevel level = LogLevel.Info)
    {
        foreach (var line in lines)
        {
            switch (level)
            {
                case LogLevel.Success:
                    Success(line);
                    break;
                case LogLevel.Warning:
                    Warn(line);
                    break;
                case LogLevel.Error:
                    Error(line);
                    break;
                default:
                    Info(line);
                    break;
            }
        }
    }

    /// <summary>由 UI sink 调用：把日志追加到日志页（自动切回 UI 线程）。</summary>
    internal static void Append(LogLevel level, string title)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => AppendCore(level, title)));
            return;
        }

        AppendCore(level, title);
    }

    private static void AppendCore(LogLevel level, string title)
    {
        Entries.Add(new LogItem
        {
            Time = DateTime.Now,
            Level = level,
            Title = title
        });

        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }

        Appended?.Invoke();
    }
}

/// <summary>把 Serilog 事件转成日志页的条目。</summary>
internal sealed class UiLogSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Warning => LogLevel.Warning,
            LogEventLevel.Error or LogEventLevel.Fatal => LogLevel.Error,
            _ => LogLevel.Info
        };

        if (logEvent.Properties.TryGetValue("UiLevel", out var marker) &&
            marker is ScalarValue { Value: "SUC" })
        {
            level = LogLevel.Success;
        }

        var message = logEvent.RenderMessage();

        if (message.StartsWith("[SUC] ", StringComparison.Ordinal))
        {
            message = message["[SUC] ".Length..];
        }

        Logger.Append(level, message);
    }
}
