using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using AngelinaISAssistant.Live.Intel;

namespace AngelinaISAssistant.Live.Ocr;

/// <summary>从 OCR 结果里识别出的当前主题、层数、关卡名。</summary>
internal sealed record RunInfo(string? Theme, int? Layer, string? LayerName, StageMatch? Stage = null);

/// <summary>
/// 把 OCR 行解释成「已进入某主题第几层」「识别到某关卡」，供遮罩左侧追加提示。
/// 主题/层数只认 tip233.txt 里的特征词和层名；关卡名来自 tomimi.dev 数据（<see cref="IsIntelData"/>），
/// 支持中/英/日三种语言的关卡名。不再把原始文字/置信度显示到遮罩上。
/// </summary>
internal sealed class RunInfoInterpreter
{
    private const string BlackCurrentTheme = "沉沦者的黑流树海";
    private const string YearGardenTheme = "岁的界园志异";

    /// <summary>所有语义提示的最低置信度（需求：统一 0.6）。</summary>
    public const float MinConfidence = 0.6f;

    private static readonly Dictionary<string, int> BlackCurrentLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["玻利瓦尔肤层"] = 1,
        ["甜美的伤口"] = 2,
        ["血色空脉"] = 3,
        ["受害者腐殖"] = 4,
        ["卡德霍之颅"] = 5,
        ["源流交汇处"] = 6,
        ["盲目者所见"] = 6
    };

    private static readonly Dictionary<string, int> YearGardenLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["洪陆楼"] = 1,
        ["山水阁"] = 2,
        ["云瓦亭"] = 3,
        ["汝吾门"] = 4,
        ["见字祠"] = 5
    };

    private static readonly Regex LayerNumberPattern = new(@"(?:第)?([1-6])\s*层", RegexOptions.Compiled);

    private readonly object _gate = new();
    private string? _theme;
    private int? _layer;
    private string? _layerName;
    private StageMatch? _stage;
    private IReadOnlyList<StageMatch> _stageMatches = Array.Empty<StageMatch>();

    /// <summary>用一帧 OCR 结果更新状态；没有新信息时保留上一次的识别结果。</summary>
    public RunInfo? Update(IReadOnlyList<OcrLine> lines, int frameWidth, int frameHeight)
    {
        var detected = Detect(lines, frameWidth, frameHeight);

        lock (_gate)
        {
            if (detected.Stages.Count > 0)
            {
                _stageMatches = detected.Stages;
                _stage = detected.Stages[0];
            }

            if (detected.Theme is not null && !string.Equals(detected.Theme, _theme, StringComparison.Ordinal))
            {
                _theme = detected.Theme;

                if (_layer is int existingLayer)
                {
                    _layerName = LookupLayerName(_theme, existingLayer);
                }
            }

            if (detected.Layer is int layer)
            {
                _layer = layer;
                _layerName = detected.LayerName ?? LookupLayerName(_theme, layer);
            }

            if (_theme is null && _layerName is not null)
            {
                _theme = ThemeOfLayerName(_layerName);
                _layer ??= LookupLayerNumber(_layerName);
            }

            // 关卡名属于受支持的主题时，即使还没识别到主题特征词，也能确定主题
            if (_theme is null
                && _stage is not null
                && ThemeProfiles.TryGet(_stage.Topic, out var profile)
                && profile.SupportsEnemyStats)
            {
                _theme = profile.Title;
            }

            if (_theme is null && _layer is null && _stage is null)
            {
                return null;
            }

            if (_layer is int knownLayer && string.IsNullOrEmpty(_layerName))
            {
                _layerName = LookupLayerName(_theme, knownLayer);
            }

            return new RunInfo(_theme, _layer, _layerName, _stage);
        }
    }

    /// <summary>当前识别到的关卡（可能有多个同名变体），供敌方情报页匹配敌人所属关卡。</summary>
    public IReadOnlyList<StageMatch> GetStageMatches()
    {
        lock (_gate)
        {
            return _stageMatches.ToArray();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _theme = null;
            _layer = null;
            _layerName = null;
            _stage = null;
            _stageMatches = Array.Empty<StageMatch>();
        }
    }

    /// <summary>
    /// 生成「已进入某主题第几层」消息；有层名时补在括号里，
    /// 例如「已进入沉沦者的黑流树海第2层（甜美的伤口）」。
    /// </summary>
    public static string? BuildOverlayMessage(RunInfo info)
    {
        string? message = null;

        if (info.Theme is not null && info.Layer is > 0)
        {
            message = $"已进入{info.Theme}第{info.Layer}层";
        }
        else if (info.Theme is not null)
        {
            message = $"已进入{info.Theme}";
        }
        else if (info.Layer is > 0)
        {
            message = $"已进入第{info.Layer}层";
        }

        if (message is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(info.LayerName))
        {
            message += $"（{info.LayerName}）";
        }

        return message;
    }

    private static DetectedInfo Detect(IReadOnlyList<OcrLine> lines, int frameWidth, int frameHeight)
    {
        string? theme = null;
        int? layer = null;
        string? layerName = null;
        IReadOnlyList<StageMatch> stages = Array.Empty<StageMatch>();

        foreach (var line in lines)
        {
            if (line.Confidence < MinConfidence)
            {
                continue;
            }

            // 关卡名：支持中/英/日三种语言的关卡名，归一化后精确匹配（位置不限）
            if (stages.Count == 0)
            {
                var stageMatches = IsIntelData.FindStages(line.Text);

                if (stageMatches.Count > 0)
                {
                    stages = stageMatches;
                }
            }

            var normalized = IsText.Normalize(line.Text);

            if (normalized.Length == 0)
            {
                continue;
            }

            // 主题特征：屏幕下方能看到的提示物
            if (line.Text.Contains("零件箱", StringComparison.Ordinal)
                || line.Text.Contains("总估价", StringComparison.Ordinal))
            {
                theme = BlackCurrentTheme;
            }
            else if (line.Text.Contains("钱盒", StringComparison.Ordinal))
            {
                theme = YearGardenTheme;
            }

            // 层数/层名：只看屏幕上方中间区域
            var centerX = line.X + line.Width / 2;
            var centerY = line.Y + line.Height / 2;
            var nearTop = centerY <= frameHeight * 0.45f;
            var nearCenter = centerX >= frameWidth * 0.15f && centerX <= frameWidth * 0.85f;

            if (!nearTop || !nearCenter)
            {
                continue;
            }

            var numberMatch = LayerNumberPattern.Match(normalized);

            if (numberMatch.Success
                && int.TryParse(numberMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                layer = number;
            }

            var matchedName = MatchLayerName(normalized);

            if (matchedName is not null)
            {
                layerName = matchedName;
                layer = LookupLayerNumber(matchedName);

                if (theme is null)
                {
                    theme = ThemeOfLayerName(matchedName);
                }
            }
        }

        return new DetectedInfo(theme, layer, layerName, stages);
    }

    private static string? MatchLayerName(string normalized)
    {
        foreach (var name in BlackCurrentLayers.Keys)
        {
            if (normalized.Contains(IsText.Normalize(name), StringComparison.Ordinal))
            {
                return name;
            }
        }

        foreach (var name in YearGardenLayers.Keys)
        {
            if (normalized.Contains(IsText.Normalize(name), StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }

    private static string? ThemeOfLayerName(string? layerName)
    {
        if (string.IsNullOrEmpty(layerName))
        {
            return null;
        }

        if (BlackCurrentLayers.ContainsKey(layerName))
        {
            return BlackCurrentTheme;
        }

        if (YearGardenLayers.ContainsKey(layerName))
        {
            return YearGardenTheme;
        }

        return null;
    }

    private static int? LookupLayerNumber(string? layerName)
    {
        if (string.IsNullOrEmpty(layerName))
        {
            return null;
        }

        if (BlackCurrentLayers.TryGetValue(layerName, out var blackLayer))
        {
            return blackLayer;
        }

        if (YearGardenLayers.TryGetValue(layerName, out var gardenLayer))
        {
            return gardenLayer;
        }

        return null;
    }

    private static string? LookupLayerName(string? theme, int layer)
    {
        if (string.IsNullOrEmpty(theme))
        {
            return null;
        }

        var source = string.Equals(theme, BlackCurrentTheme, StringComparison.Ordinal)
            ? BlackCurrentLayers
            : YearGardenLayers;

        foreach (var pair in source)
        {
            if (pair.Value == layer)
            {
                return pair.Key;
            }
        }

        return null;
    }

    private sealed record DetectedInfo(string? Theme, int? Layer, string? LayerName, IReadOnlyList<StageMatch> Stages);
}
