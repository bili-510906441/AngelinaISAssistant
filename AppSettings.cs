using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AngelinaISAssistant;

internal enum UpdatePackagePreference
{
    /// <summary>优先增量包（.aisapatch）。</summary>
    Incremental,

    /// <summary>优先完整包（_full.zip）。</summary>
    Full
}

internal enum ConfigIssue
{
    None,
    Missing,
    Corrupted,
    Unreadable
}

/// <summary>敌人数值在遮罩上的显示格式。</summary>
internal enum NumberDisplayFormat
{
    /// <summary>万（例如 35.8w，一位小数）。</summary>
    Wan,

    /// <summary>科学计数法（例如 3.58e5）。</summary>
    Scientific,

    /// <summary>完整数字（例如 358000）。</summary>
    Plain
}

/// <summary>启动时读取配置的结果，用于向用户提示“已恢复默认配置”。</summary>
internal sealed class ConfigLoadNotice
{
    public ConfigLoadNotice(ConfigIssue issue, int recoveredFields, string? backupPath, string? error = null)
    {
        Issue = issue;
        RecoveredFields = recoveredFields;
        BackupPath = backupPath;
        Error = error;
    }

    public ConfigIssue Issue { get; }

    public int RecoveredFields { get; }

    public string? BackupPath { get; }

    public string? Error { get; }
}

/// <summary>
/// 用户配置。全部数据跟随 release 目录保存在 config\settings.json，
/// 默认值以代码形式硬编码在 AngelinaISAssistant.dll 内，不依赖任何外部默认文件。
/// </summary>
internal sealed class AppSettings
{
    private static readonly string ConfigDirectory = Path.Combine(AppContext.BaseDirectory, "config");

    private static readonly string FilePath = Path.Combine(ConfigDirectory, "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static AppSettings? _current;

    public static AppSettings Current => _current ??= Load();

    /// <summary>本次启动读取配置时产生的问题；读取正常时为 null。</summary>
    internal static ConfigLoadNotice? StartupNotice { get; private set; }

    public string Language { get; set; } = string.Empty;

    public UpdatePackagePreference UpdatePreference { get; set; } = UpdatePackagePreference.Incremental;

    /// <summary>
    /// 推理加速设备：-1 = 使用 CPU，-2 = 系统默认，0 及以上的索引对应 GpuCatalog.Devices。
    /// </summary>
    public int InferenceGpu { get; set; } = -2;

    /// <summary>实时辅助使用的画面采集方式。</summary>
    public AngelinaISAssistant.Capture.CaptureBackend CaptureBackend { get; set; } = AngelinaISAssistant.Capture.CaptureBackend.Wgc;

    /// <summary>显示/收起本程序的全局热键，空字符串表示不生效。</summary>
    public string HotkeyShowHide { get; set; } = "Ctrl+Shift+Alt+A";

    /// <summary>实时辅助左侧勾选项（局外 / 局内）。</summary>
    public Live.LiveAssistOptions LiveAssist { get; set; } = Live.LiveAssistOptions.None;

    /// <summary>敌人数值在遮罩上的显示格式。</summary>
    public NumberDisplayFormat NumberFormat { get; set; } = NumberDisplayFormat.Wan;

    public bool ShowAdvancedSettings { get; set; }

    public bool SoftwareRendering { get; set; }

    /// <summary>内置默认配置（硬编码在 DLL 内，不依赖任何外部文件）。</summary>
    private static AppSettings CreateDefault() => new()
    {
        Language = string.Empty,
        UpdatePreference = UpdatePackagePreference.Incremental,
        InferenceGpu = -2,
        CaptureBackend = AngelinaISAssistant.Capture.CaptureBackend.Wgc,
        HotkeyShowHide = "Ctrl+Shift+Alt+A",
        LiveAssist = Live.LiveAssistOptions.None,
        NumberFormat = NumberDisplayFormat.Wan,
        ShowAdvancedSettings = false,
        SoftwareRendering = false
    };

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, SerializerOptions));
        }
        catch
        {
            // 保存失败不影响使用（例如只读目录）
        }
    }

    /// <summary>仅供测试：重新从磁盘加载并重置提示。</summary>
    internal static AppSettings Reload()
    {
        _current = null;
        StartupNotice = null;
        return Current;
    }

    private static AppSettings Load()
    {
        var defaults = CreateDefault();

        if (!File.Exists(FilePath))
        {
            StartupNotice = new ConfigLoadNotice(ConfigIssue.Missing, 0, null);
            _current = defaults;
            Save();
            return defaults;
        }

        string raw;

        try
        {
            raw = File.ReadAllText(FilePath);
        }
        catch (Exception ex)
        {
            StartupNotice = new ConfigLoadNotice(ConfigIssue.Unreadable, 0, null, ex.Message);
            return defaults;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch
        {
            var backup = BackupBrokenFile();
            StartupNotice = new ConfigLoadNotice(ConfigIssue.Corrupted, 0, backup);
            _current = defaults;
            Save();
            return defaults;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                var backup = BackupBrokenFile();
                StartupNotice = new ConfigLoadNotice(ConfigIssue.Corrupted, 0, backup);
                _current = defaults;
                Save();
                return defaults;
            }

            var settings = CreateDefault();
            var recognized = 0;
            var recovered = 0;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case nameof(Language):
                        recognized++;
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            settings.Language = property.Value.GetString() ?? string.Empty;
                            recovered++;
                        }
                        break;

                    case nameof(UpdatePreference):
                        recognized++;
                        if (TryEnum(property.Value, out UpdatePackagePreference preference))
                        {
                            settings.UpdatePreference = preference;
                            recovered++;
                        }
                        break;

                    case nameof(InferenceGpu):
                        recognized++;
                        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var gpu))
                        {
                            settings.InferenceGpu = gpu;
                            recovered++;
                        }
                        break;

                    case nameof(CaptureBackend):
                        recognized++;
                        if (TryEnum(property.Value, out AngelinaISAssistant.Capture.CaptureBackend backend))
                        {
                            settings.CaptureBackend = backend;
                            recovered++;
                        }
                        break;

                    case nameof(HotkeyShowHide):
                        recognized++;
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            settings.HotkeyShowHide = property.Value.GetString() ?? string.Empty;
                            recovered++;
                        }
                        break;

                    case nameof(LiveAssist):
                        recognized++;
                        if (TryEnum(property.Value, out Live.LiveAssistOptions liveAssist))
                        {
                            settings.LiveAssist = liveAssist;
                            recovered++;
                        }
                        break;

                    case nameof(NumberFormat):
                        recognized++;
                        if (TryEnum(property.Value, out NumberDisplayFormat numberFormat))
                        {
                            settings.NumberFormat = numberFormat;
                            recovered++;
                        }
                        break;

                    case nameof(ShowAdvancedSettings):
                        recognized++;
                        if (TryBool(property.Value, out var advanced))
                        {
                            settings.ShowAdvancedSettings = advanced;
                            recovered++;
                        }
                        break;

                    case nameof(SoftwareRendering):
                        recognized++;
                        if (TryBool(property.Value, out var software))
                        {
                            settings.SoftwareRendering = software;
                            recovered++;
                        }
                        break;

                    default:
                        // 未知字段（可能来自更新的版本）直接忽略，不视为损坏
                        break;
                }
            }

            if (recognized > recovered)
            {
                var backup = BackupBrokenFile();
                StartupNotice = new ConfigLoadNotice(ConfigIssue.Corrupted, recovered, backup);
                _current = settings;
                Save();
                return settings;
            }

            return settings;
        }
    }

    private static bool TryBool(JsonElement element, out bool value)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryEnum<T>(JsonElement element, out T value) where T : struct, Enum
    {
        if (element.ValueKind == JsonValueKind.String &&
            Enum.TryParse(element.GetString(), ignoreCase: true, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? BackupBrokenFile()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var backup = Path.Combine(ConfigDirectory, $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(FilePath, backup, overwrite: true);
            return Path.GetFileName(backup);
        }
        catch
        {
            return null;
        }
    }
}
