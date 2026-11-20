using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AngelinaISAssistant.Live.Intel;

/// <summary>一条关卡名匹配结果。同一关卡名可能对应多个 levelId（变体关卡）。</summary>
internal sealed record StageMatch(string Display, string LevelId, string Topic);

/// <summary>关卡 / 敌人 / 难度的静态数据（编译进 DLL 的 JSON 资源，运行时不需要外部文件）。</summary>
internal sealed class IsDataset
{
    public List<StageNameAlias> StageNames { get; init; } = new();

    public Dictionary<string, StageInfo> Stages { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, EnemyInfo> Enemies { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<DifficultyEntry>> Difficulty { get; init; } = new(StringComparer.Ordinal);

    /// <summary>归一化关卡名 -> 匹配结果列表。</summary>
    public Dictionary<string, List<StageMatch>> StageIndex { get; } = new(StringComparer.Ordinal);

    internal void BuildIndex()
    {
        foreach (var alias in StageNames)
        {
            var key = IsText.Normalize(alias.Text);

            if (key.Length < 2 || string.IsNullOrEmpty(alias.LevelId))
            {
                continue;
            }

            if (!StageIndex.TryGetValue(key, out var matches))
            {
                matches = new List<StageMatch>();
                StageIndex[key] = matches;
            }

            var match = new StageMatch(alias.Display, alias.LevelId, alias.Topic);

            if (!matches.Contains(match))
            {
                matches.Add(match);
            }
        }
    }
}

/// <summary>
/// 集成战略静态数据入口：关卡名匹配、关卡信息、敌人模板、难度修正。
/// 数据由 version_tools/generate_intel_data.py 从 tomimi.dev 生成，编译进 DLL。
/// </summary>
internal static class IsIntelData
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<IsDataset> Lazy = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private static int _failureReported;

    /// <summary>数据是否可用；资源缺失或解析失败时为 false（功能自动降级为不显示）。</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                _ = Lazy.Value;
                return true;
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _failureReported, 1) == 0)
                {
                    Logger.Warn($"intel data unavailable · {ex.Message}");
                }

                return false;
            }
        }
    }

    public static IsDataset Data => Lazy.Value;

    /// <summary>按 OCR 文字精确匹配关卡名（已归一化）。</summary>
    public static bool TryMatchStage(string? text, out StageMatch match)
    {
        match = default!;

        if (!IsAvailable)
        {
            return false;
        }

        var key = IsText.Normalize(text);

        if (key.Length < 2 || !Data.StageIndex.TryGetValue(key, out var matches) || matches.Count == 0)
        {
            return false;
        }

        match = matches[0];
        return true;
    }

    /// <summary>返回全部同名关卡（变体关卡会有多个 levelId）。</summary>
    public static IReadOnlyList<StageMatch> FindStages(string? text)
    {
        if (!IsAvailable)
        {
            return Array.Empty<StageMatch>();
        }

        var key = IsText.Normalize(text);

        if (key.Length < 2 || !Data.StageIndex.TryGetValue(key, out var matches))
        {
            return Array.Empty<StageMatch>();
        }

        return matches;
    }

    public static StageInfo? GetStage(string levelId) =>
        IsAvailable && Data.Stages.TryGetValue(levelId, out var stage) ? stage : null;

    public static EnemyInfo? GetEnemy(string key) =>
        IsAvailable && Data.Enemies.TryGetValue(key, out var enemy) ? enemy : null;

    /// <summary>某敌人的全部语言名字（缺失的名字会跳过）。</summary>
    public static IReadOnlyList<string> GetEnemyNames(string key)
    {
        var enemy = GetEnemy(key);

        if (enemy is null)
        {
            return Array.Empty<string>();
        }

        return enemy.Names.Values
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();
    }

    /// <summary>某主题的难度表（0 到最大难度）。</summary>
    public static IReadOnlyList<DifficultyEntry> GetDifficulty(string topic) =>
        IsAvailable && Data.Difficulty.TryGetValue(topic, out var entries)
            ? entries
            : Array.Empty<DifficultyEntry>();

    /// <summary>
    /// 为一组敌人构建「归一化敌人名 -> 敌人 key」索引（中/英/日三种名字都收进去）。
    /// 关卡情报页只显示当前关卡的敌人，按关卡敌人列表建索引可以避免全库重名误判。
    /// </summary>
    public static Dictionary<string, string> BuildEnemyNameIndex(IEnumerable<string> enemyKeys)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in enemyKeys)
        {
            var enemy = GetEnemy(key);

            if (enemy is null)
            {
                continue;
            }

            foreach (var name in enemy.Names.Values)
            {
                var normalized = IsText.Normalize(name);

                if (normalized.Length == 0 || index.ContainsKey(normalized))
                {
                    continue;
                }

                index[normalized] = key;
            }
        }

        return index;
    }

    private static IsDataset Load()
    {
        var assembly = typeof(IsIntelData).Assembly;

        var dataset = new IsDataset
        {
            StageNames = LoadResource<List<StageNameAlias>>(assembly, "stage_names.json"),
            Stages = LoadResource<Dictionary<string, StageInfo>>(assembly, "stages.json"),
            Enemies = LoadResource<Dictionary<string, EnemyInfo>>(assembly, "enemies.json"),
            Difficulty = LoadResource<Dictionary<string, List<DifficultyEntry>>>(assembly, "difficulty.json")
        };

        dataset.BuildIndex();
        return dataset;
    }

    private static T LoadResource<T>(Assembly assembly, string suffix)
    {
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new FileNotFoundException($"内嵌数据缺失：{suffix}");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new FileNotFoundException($"内嵌数据无法读取：{suffix}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();

        return JsonSerializer.Deserialize<T>(json, SerializerOptions)
               ?? throw new InvalidDataException($"内嵌数据解析失败：{suffix}");
    }
}

/// <summary>楼层成长公式类型。</summary>
internal enum FloorBuffKind
{
    /// <summary>没有楼层成长数据。</summary>
    None,

    /// <summary>每层 hp/atk 各自乘以 floorBuff 的「层数」次方（黑流树海 / 岁的界园志异）。</summary>
    ObjectPower
}

/// <summary>主题的数值计算配置。</summary>
internal sealed class ThemeProfile
{
    public required string Topic { get; init; }

    public required string Title { get; init; }

    public int MaxDifficulty { get; init; }

    public FloorBuffKind FloorBuff { get; init; }

    /// <summary>是否已经支持「敌人数值」计算；未支持的主题只识别关卡名/难度，不计算数值。</summary>
    public bool SupportsEnemyStats { get; init; }
}

/// <summary>主题配置（楼层成长公式、最大难度、是否支持敌人数值计算）。</summary>
internal static class ThemeProfiles
{
    private static readonly Dictionary<string, ThemeProfile> Profiles = new(StringComparer.Ordinal)
    {
        ["rogue_phantom"] = new()
        {
            Topic = "rogue_phantom",
            Title = "傀影与猩红孤钻",
            MaxDifficulty = 15,
            FloorBuff = FloorBuffKind.None
        },
        ["rogue_mizuki"] = new()
        {
            Topic = "rogue_mizuki",
            Title = "水月与深蓝之树",
            MaxDifficulty = 18,
            FloorBuff = FloorBuffKind.ObjectPower
        },
        ["rogue_sami"] = new()
        {
            Topic = "rogue_sami",
            Title = "探索者的银凇止境",
            MaxDifficulty = 15,
            FloorBuff = FloorBuffKind.None
        },
        ["rogue_skz"] = new()
        {
            Topic = "rogue_skz",
            Title = "萨卡兹的无终奇语",
            MaxDifficulty = 18,
            FloorBuff = FloorBuffKind.None
        },
        ["rogue_yan"] = new()
        {
            Topic = "rogue_yan",
            Title = "岁的界园志异",
            MaxDifficulty = 18,
            FloorBuff = FloorBuffKind.ObjectPower,
            SupportsEnemyStats = true
        },
        ["rogue_6"] = new()
        {
            Topic = "rogue_6",
            Title = "沉沦者的黑流树海",
            MaxDifficulty = 16,
            FloorBuff = FloorBuffKind.ObjectPower,
            SupportsEnemyStats = true
        }
    };

    public static bool TryGet(string? topic, out ThemeProfile profile)
    {
        profile = null!;

        return !string.IsNullOrEmpty(topic) && Profiles.TryGetValue(topic, out profile!);
    }

    public static string? TitleOf(string? topic) =>
        !string.IsNullOrEmpty(topic) && Profiles.TryGetValue(topic, out var profile) ? profile.Title : null;
}
