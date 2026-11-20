using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AngelinaISAssistant.Live.Ocr;

namespace AngelinaISAssistant.Live.Intel;

/// <summary>
/// 「敌方情报」页识别：检测该页面（全部/精英/领袖 + 五个属性标签），识别当前查看的敌人，
/// 计算该敌人在当前难度/层数下的数值，并生成显示在属性标签右侧的文字标注。
/// </summary>
internal sealed class EnemyIntelReader
{
    private const float MinConfidence = 0.6f;

    /// <summary>页面顶部的分类标签（需求里要求全部识别到才认为打开了敌方情报页）。</summary>
    private static readonly string[] TabLabels = { "全部", "精英", "领袖" };

    /// <summary>页面判定必须识别到的属性标签（需求里的五个属性）。</summary>
    private static readonly string[] RequiredStatLabels =
    {
        "生命值", "攻击力", "防御力", "法术抗性", "移动速度"
    };

    /// <summary>需要显示数值的属性（顺序即绘制顺序）：左边四个 + 右边四个。</summary>
    private static readonly (string Label, string StatKey)[] DisplayStatLabels =
    {
        ("生命值", "hp"),
        ("攻击力", "atk"),
        ("防御力", "def"),
        ("法术抗性", "res"),
        ("移动速度", "ms"),
        ("攻击速度", "aspd"),
        ("元素抗性", "epRes"),
        ("损伤抵抗", "epDmg")
    };

    /// <summary>游戏里属性用字母等级显示（S/A/B/C/D，可能带 +）。</summary>
    private static readonly Regex GradePattern = new("^[SABCDE][+-]?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, Dictionary<string, string>> _enemyNameIndexCache = new(StringComparer.Ordinal);

    private IReadOnlyList<OverlayAnnotation> _last = Array.Empty<OverlayAnnotation>();
    private int _misses;

    /// <summary>当前帧是否停在敌方情报页（供后端决定是否参与结果）。</summary>
    public bool LastFrameWasPanel { get; private set; }

    /// <summary>最近一次匹配到的敌人 key（日志/测试用）。</summary>
    public string? LastEnemyKey { get; private set; }

    /// <summary>最近一次计算出的数值结果（日志/测试用）。</summary>
    public EnemyStatResult? LastStatResult { get; private set; }

    public void Reset()
    {
        _last = Array.Empty<OverlayAnnotation>();
        _misses = 0;
        LastFrameWasPanel = false;
        LastEnemyKey = null;
        LastStatResult = null;
    }

    /// <summary>
    /// 处理一帧 OCR 结果：页面存在时返回数值标注；刚消失时保留一帧避免闪烁。
    /// </summary>
    public IReadOnlyList<OverlayAnnotation> Update(
        IReadOnlyList<OcrLine> lines,
        int frameWidth,
        int frameHeight,
        IReadOnlyList<StageMatch> stages,
        int? difficulty,
        int? layer,
        bool eliteMode)
    {
        LastFrameWasPanel = false;

        var confident = lines.Where(line => line.Confidence >= MinConfidence).ToList();

        if (!TryFindPanel(confident, frameHeight, out var tabs, out var labelBoxes))
        {
            _misses++;

            if (_misses <= 1)
            {
                return _last;
            }

            _last = Array.Empty<OverlayAnnotation>();
            return _last;
        }

        LastFrameWasPanel = true;
        _misses = 0;
        LastEnemyKey = null;
        LastStatResult = null;

        if (difficulty is not int currentDifficulty
            || layer is not int currentLayer
            || stages.Count == 0)
        {
            _last = Array.Empty<OverlayAnnotation>();
            return _last;
        }

        var found = FindViewedEnemy(confident, tabs, labelBoxes, stages, frameWidth, eliteMode);

        if (found is null)
        {
            _last = Array.Empty<OverlayAnnotation>();
            return _last;
        }

        var profile = ThemeProfiles.TryGet(found.Value.Topic, out var themeProfile) ? themeProfile : null;

        if (profile is null || !profile.SupportsEnemyStats)
        {
            _last = Array.Empty<OverlayAnnotation>();
            return _last;
        }

        var result = EnemyStatCalculator.Compute(new EnemyStatContext(
            found.Value.Topic,
            found.Value.LevelId,
            found.Value.EnemyKey,
            currentDifficulty,
            currentLayer,
            eliteMode));

        if (result is null)
        {
            _last = Array.Empty<OverlayAnnotation>();
            return _last;
        }

        LastEnemyKey = result.EnemyKey;
        LastStatResult = result;
        _last = BuildAnnotations(confident, labelBoxes, result.Values, frameWidth);
        return _last;
    }

    /// <summary>检测敌方情报页：三个分类标签 + 五个属性标签全部识别到才算。</summary>
    private static bool TryFindPanel(
        List<OcrLine> lines,
        int frameHeight,
        out List<OcrLine> tabs,
        out Dictionary<string, OcrLine> labelBoxes)
    {
        tabs = new List<OcrLine>();
        labelBoxes = new Dictionary<string, OcrLine>(StringComparer.Ordinal);

        // 分类标签在屏幕顶部；OCR 可能把「全部 精英 领袖」连成一行，因此按「包含」判断
        var topLimit = frameHeight * 0.30;

        foreach (var tab in TabLabels)
        {
            var normalizedTab = IsText.Normalize(tab);
            var best = lines
                .Where(line =>
                    line.Y + line.Height / 2 <= topLimit
                    && IsText.Normalize(line.Text).Contains(normalizedTab, StringComparison.Ordinal))
                .OrderByDescending(line => line.Confidence)
                .FirstOrDefault();

            if (best is null)
            {
                return false;
            }

            tabs.Add(best);
        }

        // 八个属性标签：能识别到的都记下来显示数值；其中需求里的五个是判定页面的必要条件
        foreach (var (label, _) in DisplayStatLabels)
        {
            var normalizedLabel = IsText.Normalize(label);

            // 属性标签左边会带着图标（+ / × / 盾牌…），OCR 常把图标并进文字（例如「十生命值」），
            // 所以允许标签前后多出 1~2 个字符；同时用长度限制排除描述文字里的同名词语。
            var best = lines
                .Where(line =>
                {
                    var normalized = IsText.Normalize(line.Text);

                    return normalized.Contains(normalizedLabel, StringComparison.Ordinal)
                           && normalized.Length <= normalizedLabel.Length + 2;
                })
                .OrderByDescending(line => line.Confidence)
                .ThenBy(line => line.Width)
                .FirstOrDefault();

            if (best is not null)
            {
                labelBoxes[label] = best;
            }
        }

        foreach (var required in RequiredStatLabels)
        {
            if (!labelBoxes.ContainsKey(required))
            {
                return false;
            }
        }

        return true;
    }

    private (string LevelId, string Topic, string EnemyKey)? FindViewedEnemy(
        List<OcrLine> lines,
        List<OcrLine> tabs,
        Dictionary<string, OcrLine> labelBoxes,
        IReadOnlyList<StageMatch> stages,
        int frameWidth,
        bool eliteMode)
    {
        var tabBottom = tabs.Max(tab => tab.Y + tab.Height);
        var statTop = labelBoxes.Values.Min(label => label.Y);

        var candidates = lines
            .Where(line =>
            {
                var centerX = line.X + line.Width / 2;

                return centerX >= frameWidth * 0.45 // 右侧详情区（相对位置，UI 缩放变化也不影响）
                       && line.Y >= tabBottom
                       && line.Y + line.Height <= statTop;
            })
            .OrderByDescending(line => line.Height)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var stage in stages)
        {
            var index = GetEnemyNameIndex(stage.LevelId, eliteMode);

            if (index is null)
            {
                continue;
            }

            // 1) 归一化后精确命中敌人名
            foreach (var candidate in candidates)
            {
                var normalized = IsText.Normalize(candidate.Text);

                if (normalized.Length == 0)
                {
                    continue;
                }

                if (index.TryGetValue(normalized, out var enemyKey))
                {
                    return (stage.LevelId, stage.Topic, enemyKey);
                }
            }

            // 2) 兜底：OCR 行把敌人名和别的字粘在一起时，按「包含 + 最长命中」匹配
            foreach (var candidate in candidates)
            {
                var normalized = IsText.Normalize(candidate.Text);
                var matched = index.Keys
                    .Where(key => key.Length >= 3 && normalized.Contains(key, StringComparison.Ordinal))
                    .OrderByDescending(key => key.Length)
                    .FirstOrDefault();

                if (matched is not null)
                {
                    return (stage.LevelId, stage.Topic, index[matched]);
                }
            }
        }

        return null;
    }

    /// <summary>某关卡所有变体里出现过的敌人名索引（归一化名字 -> 敌人 key），按关卡 + 是否紧急作战缓存。</summary>
    private Dictionary<string, string>? GetEnemyNameIndex(string levelId, bool eliteMode)
    {
        var cacheKey = eliteMode ? levelId + "|elite" : levelId;

        if (_enemyNameIndexCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var stage = IsIntelData.GetStage(levelId);

        if (stage is null)
        {
            return null;
        }

        var keys = stage.Variants
            .SelectMany(variant => variant.Enemies)
            .Select(enemy => enemy.Key)
            .ToList();

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);

        if (eliteMode)
        {
            foreach (var variant in stage.Variants)
            {
                if (variant.EliteRunes is null)
                {
                    continue;
                }

                foreach (var pair in variant.EliteRunes.EnemyReplace)
                {
                    replacements[pair.Key] = pair.Value;
                    keys.Add(pair.Value);
                }
            }
        }

        var index = IsIntelData.BuildEnemyNameIndex(keys.Distinct(StringComparer.Ordinal));

        if (eliteMode)
        {
            foreach (var pair in replacements)
            {
                // 紧急作战里被替换的敌人：原名/精英名都指向精英版
                foreach (var name in IsIntelData.GetEnemyNames(pair.Key).Concat(IsIntelData.GetEnemyNames(pair.Value)))
                {
                    var normalized = IsText.Normalize(name);

                    if (normalized.Length > 0)
                    {
                        index[normalized] = pair.Value;
                    }
                }
            }
        }

        _enemyNameIndexCache[cacheKey] = index;
        return index;
    }

    /// <summary>把计算出的五个数值放到对应属性标签右侧（在游戏自带的字母等级前面）。</summary>
    private static IReadOnlyList<OverlayAnnotation> BuildAnnotations(
        List<OcrLine> lines,
        Dictionary<string, OcrLine> labelBoxes,
        EnemyStatValues values,
        int frameWidth)
    {
        var statValues = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["hp"] = values.Hp,
            ["atk"] = values.Atk,
            ["def"] = values.Def,
            ["res"] = values.Res,
            ["ms"] = values.Ms,
            ["aspd"] = values.Aspd,
            ["epRes"] = values.EpRes,
            ["epDmg"] = values.EpDmg
        };

        var format = AppSettings.Current.NumberFormat;
        var annotations = new List<OverlayAnnotation>(DisplayStatLabels.Length);

        foreach (var (label, statKey) in DisplayStatLabels)
        {
            if (!labelBoxes.TryGetValue(label, out var box) || !statValues.TryGetValue(statKey, out var value))
            {
                continue;
            }

            var grade = FindGradeToken(lines, box);
            var labelRight = box.X + box.Width + 6;

            // 同一行右侧还有属性标签 => 当前是左列；右列后面是屏幕边缘
            var neighbor = FindRightNeighbor(labelBoxes.Values, box);
            double rightEdge;
            double maxWidth;
            var alignRight = true;

            if (grade is not null)
            {
                var gap = grade.X - 6 - labelRight;
                var afterGrade = grade.X + grade.Width + 8;
                var availableAfter = neighbor is null
                    ? frameWidth - afterGrade - 10
                    : neighbor.X - afterGrade - 8;

                if (neighbor is null && availableAfter >= 60)
                {
                    // 右列：统一放在字母等级右侧，和右列其它数值对齐
                    rightEdge = afterGrade;
                    maxWidth = availableAfter;
                    alignRight = false;
                }
                else if (gap >= 28)
                {
                    // 左列：放在属性标签和字母等级之间
                    rightEdge = grade.X - 6;
                    maxWidth = gap;
                }
                else if (availableAfter >= 40)
                {
                    rightEdge = afterGrade;
                    maxWidth = availableAfter;
                    alignRight = false;
                }
                else
                {
                    rightEdge = grade.X - 6;
                    maxWidth = Math.Max(24, gap);
                }
            }
            else
            {
                // 找不到字母时退化为「标签右侧左对齐」
                rightEdge = labelRight;
                maxWidth = 180;
                alignRight = false;
            }

            var fontSize = Math.Clamp(box.Height * 0.72, 14, 30);
            var y = box.Y + box.Height / 2 - fontSize * 0.62;

            annotations.Add(new OverlayAnnotation(
                NumberDisplay.Format(value, format),
                rightEdge,
                y,
                fontSize,
                maxWidth,
                alignRight));
        }

        return annotations;
    }

    /// <summary>找同一行右侧的另一个属性标签：有它说明当前是左列，数字应放在标签和字母等级之间。</summary>
    private static OcrLine? FindRightNeighbor(IEnumerable<OcrLine> boxes, OcrLine box)
    {
        var centerY = box.Y + box.Height / 2;
        OcrLine? best = null;
        var bestX = double.MaxValue;

        foreach (var other in boxes)
        {
            if (ReferenceEquals(other, box))
            {
                continue;
            }

            var otherCenterY = other.Y + other.Height / 2;

            if (Math.Abs(otherCenterY - centerY) > Math.Max(box.Height, other.Height) * 0.9)
            {
                continue;
            }

            if (other.X <= box.X + box.Width || other.X >= bestX)
            {
                continue;
            }

            bestX = other.X;
            best = other;
        }

        return best;
    }

    /// <summary>找属性标签右侧同一行里的字母等级（S+ / A / B ...）。</summary>
    private static OcrLine? FindGradeToken(List<OcrLine> lines, OcrLine label)
    {
        var labelCenterY = label.Y + label.Height / 2;
        var labelRight = label.X + label.Width;
        OcrLine? best = null;
        var bestDistance = double.MaxValue;

        foreach (var line in lines)
        {
            if (!GradePattern.IsMatch(line.Text.Trim()))
            {
                continue;
            }

            var centerY = line.Y + line.Height / 2;

            if (Math.Abs(centerY - labelCenterY) > label.Height * 0.9)
            {
                continue;
            }

            if (line.X < labelRight - 10)
            {
                continue;
            }

            var distance = line.X - labelRight;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = line;
            }
        }

        return best;
    }
}
