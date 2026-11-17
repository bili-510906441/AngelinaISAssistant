using System;
using System.IO;
using System.Text;

namespace AngelinaISAssistant;

/// <summary>崩溃报告：写入文件，并在进程被强制终止时留下标记供下次启动提示。</summary>
internal static class CrashReporter
{
    /// <summary>仅用于测试：指定报告根目录，留空则使用 %AppData%\AngelinaISAssistant。</summary>
    internal static string? OverrideRoot { get; set; }

    private static string DataDirectory => OverrideRoot ?? Path.Combine(AppContext.BaseDirectory, "reports");

    public static string ReportDirectory => DataDirectory;

    private static string PendingFile => Path.Combine(DataDirectory, "pending_crash.txt");

    public static string Write(Exception exception, string context)
    {
        try
        {
            Directory.CreateDirectory(ReportDirectory);

            var path = Path.Combine(ReportDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            File.WriteAllText(path, Build(exception, context), Encoding.UTF8);
            return path;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void MarkPending(string reportPath)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(PendingFile, reportPath, Encoding.UTF8);
        }
        catch
        {
            // 记不下来时只能下次启动时没有提示
        }
    }

    public static string? TryConsumePending()
    {
        try
        {
            if (!File.Exists(PendingFile))
            {
                return null;
            }

            var path = File.ReadAllText(PendingFile).Trim();
            File.Delete(PendingFile);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从报告文件里取出第一条异常的「类型 + 消息」作为简短原因。</summary>
    public static string TryReadReason(string reportPath)
    {
        try
        {
            if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath))
            {
                return string.Empty;
            }

            var type = string.Empty;
            var message = string.Empty;

            foreach (var line in File.ReadAllLines(reportPath))
            {
                if (line.StartsWith("Type     : ", StringComparison.Ordinal) && type.Length == 0)
                {
                    type = line["Type     : ".Length..].Trim();
                }
                else if (line.StartsWith("Message  : ", StringComparison.Ordinal) && message.Length == 0)
                {
                    message = line["Message  : ".Length..].Trim();
                }

                if (type.Length > 0 && message.Length > 0)
                {
                    break;
                }
            }

            return message.Length == 0 ? type : $"{type}: {message}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Build(Exception exception, string context)
    {
        var builder = new StringBuilder();

        builder.AppendLine("AngelinaISAssistant crash report");
        builder.AppendLine($"Time     : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        builder.AppendLine($"Version  : {AppInfo.Version}");
        builder.AppendLine($"Platform : {PlatformInfo.Current}");
        builder.AppendLine($"Runtime  : .NET {Environment.Version}");
        builder.AppendLine($"OS       : {Environment.OSVersion}");
        builder.AppendLine($"Context  : {context}");
        builder.AppendLine();

        var current = exception;
        var depth = 0;

        while (current is not null)
        {
            builder.AppendLine($"--- exception #{depth} ---");
            builder.AppendLine($"Type     : {current.GetType().FullName}");
            builder.AppendLine($"HResult  : 0x{current.HResult:X8}");
            builder.AppendLine($"Message  : {current.Message}");
            builder.AppendLine(current.StackTrace);
            builder.AppendLine();

            current = current.InnerException;
            depth++;
        }

        return builder.ToString();
    }
}
