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

/// <summary>用户设置，保存在 %AppData%\AngelinaISAssistant\settings.json。</summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AngelinaISAssistant",
        "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static AppSettings? _current;

    public static AppSettings Current => _current ??= Load();

    /// <summary>界面语言：zh-CN / zh-TW / en-US / ja-JP，空表示跟随系统。</summary>
    public string Language { get; set; } = string.Empty;

    public UpdatePackagePreference UpdatePreference { get; set; } = UpdatePackagePreference.Incremental;

    /// <summary>
    /// 推理加速设备：-1 = 使用 CPU，-2 = 系统默认，0 及以上 = GpuCatalog.Devices 的索引。
    /// </summary>
    public int InferenceGpu { get; set; } = -2;

    /// <summary>实时辅助使用的画面采集方式。</summary>
    public AngelinaISAssistant.Capture.CaptureBackend CaptureBackend { get; set; } = AngelinaISAssistant.Capture.CaptureBackend.Wgc;

    /// <summary>界面是否强制使用软件渲染（RenderOptions.ProcessRenderMode = SoftwareOnly）。</summary>
    /// <summary>设置页是否显示高级设置（性能设置）。</summary>
    /// <summary>显示/收起本程序的全局热键，空字符串表示不生效。</summary>
    public string HotkeyShowHide { get; set; } = "Ctrl+Shift+Alt+A";

    public bool ShowAdvancedSettings { get; set; }

    public bool SoftwareRendering { get; set; }

    public static void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, SerializerOptions));
        }
        catch
        {
            // 保存失败不影响使用
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // 配置损坏时回退到默认值
        }

        return new AppSettings();
    }
}
