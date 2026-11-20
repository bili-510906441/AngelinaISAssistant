using System;
using System.IO;

internal static class Program
{
    private static int Main()
    {
        var configDir = Path.Combine(AppContext.BaseDirectory, "config");
        var settingsPath = Path.Combine(configDir, "settings.json");
        Directory.CreateDirectory(configDir);

        var failed = 0;

        // 1) 损坏文件：可读字段保留，损坏字段回默认，生成备份并标记 Corrupted
        File.WriteAllText(settingsPath,
            "{ \"Language\": \"ja-JP\", \"InferenceGpu\": \"oops\", \"ShowAdvancedSettings\": true, \"LiveAssist\": \"BattleAssist, EnemyHealth\", \"NumberFormat\": \"Scientific\" }");

        var salvaged = AngelinaISAssistant.AppSettings.Reload();

        Console.WriteLine($"corrupt: lang={salvaged.Language} gpu={salvaged.InferenceGpu} adv={salvaged.ShowAdvancedSettings} live={salvaged.LiveAssist} issue={AngelinaISAssistant.AppSettings.StartupNotice?.Issue} recovered={AngelinaISAssistant.AppSettings.StartupNotice?.RecoveredFields} backup={AngelinaISAssistant.AppSettings.StartupNotice?.BackupPath}");

        failed += Check(salvaged.Language == "ja-JP", "可读字段 Language 应保留");
        failed += Check(salvaged.InferenceGpu == -2, "损坏字段 InferenceGpu 应回默认");
        failed += Check(salvaged.ShowAdvancedSettings, "可读字段 ShowAdvancedSettings 应保留");
        failed += Check(salvaged.NumberFormat == AngelinaISAssistant.NumberDisplayFormat.Scientific, "可读字段 NumberFormat 应保留");
        failed += Check(salvaged.LiveAssist == (AngelinaISAssistant.Live.LiveAssistOptions.BattleAssist | AngelinaISAssistant.Live.LiveAssistOptions.EnemyHealth), "LiveAssist 标志位应保留");
        failed += Check(AngelinaISAssistant.AppSettings.StartupNotice?.Issue == AngelinaISAssistant.ConfigIssue.Corrupted, "状态应为 Corrupted");
        failed += Check(AngelinaISAssistant.AppSettings.StartupNotice?.RecoveredFields == 4, "恢复字段数应为 4");
        failed += Check(File.Exists(Path.Combine(configDir, AngelinaISAssistant.AppSettings.StartupNotice!.BackupPath!)), "应生成损坏备份");

        // 2) 缺失文件：全部默认，标记 Missing
        File.Delete(settingsPath);
        var missing = AngelinaISAssistant.AppSettings.Reload();

        Console.WriteLine($"missing: lang='{missing.Language}' gpu={missing.InferenceGpu} hotkey={missing.HotkeyShowHide} issue={AngelinaISAssistant.AppSettings.StartupNotice?.Issue}");

        failed += Check(missing.Language.Length == 0, "缺失时应使用默认语言（跟随系统）");
        failed += Check(missing.InferenceGpu == -2, "缺失时应使用默认推理设备");
        failed += Check(missing.HotkeyShowHide == "Ctrl+Shift+Alt+A", "缺失时应使用默认热键");
        failed += Check(missing.NumberFormat == AngelinaISAssistant.NumberDisplayFormat.Wan, "缺失时应使用默认数值显示格式");
        failed += Check(AngelinaISAssistant.AppSettings.StartupNotice?.Issue == AngelinaISAssistant.ConfigIssue.Missing, "状态应为 Missing");

        // 3) 正常文件：不产生提示
        File.WriteAllText(settingsPath, "{ \"Language\": \"en-US\", \"InferenceGpu\": -1, \"HotkeyShowHide\": \"Ctrl+Alt+K\", \"NumberFormat\": \"Plain\" }");
        var normal = AngelinaISAssistant.AppSettings.Reload();

        Console.WriteLine($"normal: lang={normal.Language} gpu={normal.InferenceGpu} hotkey={normal.HotkeyShowHide} notice={AngelinaISAssistant.AppSettings.StartupNotice?.Issue}");

        failed += Check(normal.Language == "en-US" && normal.InferenceGpu == -1, "正常文件应读取成功");
        failed += Check(normal.NumberFormat == AngelinaISAssistant.NumberDisplayFormat.Plain, "NumberFormat 应读取成功");
        failed += Check(AngelinaISAssistant.AppSettings.StartupNotice is null, "正常文件不应产生提示");

        Console.WriteLine(failed == 0 ? "CONFIG OK" : $"CONFIG FAIL ({failed})");
        return failed == 0 ? 0 : 1;
    }

    private static int Check(bool condition, string message)
    {
        if (!condition)
        {
            Console.WriteLine("  [FAIL] " + message);
        }

        return condition ? 0 : 1;
    }
}
