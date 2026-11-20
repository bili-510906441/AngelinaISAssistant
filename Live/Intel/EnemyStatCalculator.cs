using System;
using System.Collections.Generic;
using System.Linq;

namespace AngelinaISAssistant.Live.Intel;

/// <summary>一次敌人数值计算需要的上下文。</summary>
internal sealed record EnemyStatContext(
    string Topic,
    string StageLevelId,
    string EnemyKey,
    int Difficulty,
    int Layer,
    bool EliteMode);

/// <summary>计算后的敌人数值结果。</summary>
internal sealed record EnemyStatResult(
    string EnemyKey,
    string? EnemyName,
    string StageLevelId,
    int Difficulty,
    int Layer,
    EnemyStatValues Values);

/// <summary>
/// 外部修正来源的收集器：后续接入收藏品、敌人临时数值等功能时通过它追加修正。
/// </summary>
internal sealed class EnemyStatModifierSink
{
    /// <summary>追加到 statMods.others 的修正组（顺序在楼层成长之后，与 tomimi 的遗物位置一致）。</summary>
    public List<ModGroupData> Others { get; } = new();

    /// <summary>额外楼层数（例如收藏品「追忆」会让楼层成长 +1）。</summary>
    public int ExtraFloorCount { get; set; }
}

/// <summary>
/// 敌人数值的外部修正来源接口：收藏品（遗物）、敌人临时数值（例如 ISW-DF 止变里
/// 「朔壳」随敛傲装置增多而提高攻击力）等功能实现本接口后注册到
/// <see cref="EnemyStatCalculator"/> 即可。
/// </summary>
internal interface IEnemyStatModifierProvider
{
    /// <summary>收集该敌人额外的修正；没有时不做任何事。</summary>
    void Collect(EnemyStatContext context, EnemyStatModifierSink sink);
}

/// <summary>
/// 敌人数值计算入口：基础数值 + 关卡修正 + 难度修正 + 楼层成长（+ 以后接入的收藏品/临时数值），
/// 公式与 tomimi.dev 的 parseStats 对齐。
/// </summary>
internal static class EnemyStatCalculator
{
    /// <summary>外部修正来源；接入收藏品 / 临时数值时在这里注册即可。</summary>
    private static readonly IEnemyStatModifierProvider[] Providers = Array.Empty<IEnemyStatModifierProvider>();

    /// <summary>计算某个敌人在当前关卡 / 难度 / 层数下的数值；数据不足时返回 null。</summary>
    public static EnemyStatResult? Compute(EnemyStatContext context)
    {
        if (!ThemeProfiles.TryGet(context.Topic, out var profile) || !profile.SupportsEnemyStats)
        {
            return null;
        }

        var stage = IsIntelData.GetStage(context.StageLevelId);

        if (stage is null || stage.Variants.Count == 0)
        {
            return null;
        }

        var variant = stage.Variants.FirstOrDefault(item =>
                          item.Enemies.Any(enemy => enemy.Key == context.EnemyKey))
                      ?? stage.Variants[0];

        // 紧急作战：部分敌人会被替换成精英版（例如 炮手 -> 炮击组长），
        // 与 tomimi 的 elite_runes.enemy_replace 一致，计算时使用替换后的敌人。
        var effectiveKey = context.EnemyKey;

        if (context.EliteMode
            && variant.EliteRunes?.EnemyReplace.TryGetValue(context.EnemyKey, out var replacedKey) == true
            && !string.IsNullOrEmpty(replacedKey))
        {
            effectiveKey = replacedKey;
        }

        var enemyRef = variant.Enemies.FirstOrDefault(enemy => enemy.Key == effectiveKey)
                       ?? variant.Enemies.FirstOrDefault(enemy => enemy.Key == context.EnemyKey);
        var enemyInfo = IsIntelData.GetEnemy(effectiveKey) ?? IsIntelData.GetEnemy(context.EnemyKey);

        if (enemyInfo is null || enemyInfo.Stats.Count == 0)
        {
            return null;
        }

        var level = Math.Clamp(enemyRef?.Level ?? 0, 0, enemyInfo.Stats.Count - 1);
        var statsRow = enemyInfo.Stats[level];

        var entity = new StatEntity
        {
            Key = effectiveKey,
            Id = enemyRef?.Id ?? enemyInfo.Id ?? string.Empty,
            StageId = enemyRef?.Id,
            Type = enemyInfo.Type
        };

        var difficulty = Math.Clamp(context.Difficulty, 0, profile.MaxDifficulty);

        // ---- 关卡修正（runes）----
        var runeGroups = new List<List<EffectDef?>?>
        {
            context.EliteMode ? variant.EliteMods : variant.NMods,
            variant.AllMods
        };

        var runes = new ModGroupData(
            context.EliteMode ? "elite_ops" : "combat_ops",
            runeGroups);

        // ---- 难度修正：difficulty <= 当前难度的所有效果 ----
        var difficultyEffects = IsIntelData
            .GetDifficulty(context.Topic)
            .Where(entry => entry.Difficulty <= difficulty && entry.Effects is { Count: > 0 })
            .Select(entry => (List<EffectDef?>?)entry.Effects)
            .ToList();

        var diff = difficultyEffects.Count > 0
            ? new ModGroupData("difficulty", difficultyEffects)
            : null;

        // ---- 其它修正：楼层成长 / 低难度补偿 / 外部来源 ----
        var others = new List<ModGroupData>();

        var sink = new EnemyStatModifierSink();

        foreach (var provider in Providers)
        {
            provider.Collect(context, sink);
        }

        var floorGroup = BuildFloorGroup(context.Topic, difficulty, context.Layer, sink.ExtraFloorCount);

        if (floorGroup is not null)
        {
            others.Add(floorGroup);
        }

        var lowDifficulty = BuildLowDifficultyEffects(context.Topic, difficulty);

        if (lowDifficulty is not null)
        {
            others.Add(new ModGroupData("low_diff", new List<List<EffectDef?>?> { lowDifficulty }));
        }

        others.AddRange(sink.Others);

        // ---- 形态修正（form_mods[0]）----
        List<ModDef?>? formMods = null;

        if (statsRow.FormMods is { Count: > 0 })
        {
            formMods = statsRow.FormMods[0];
        }

        var values = StatMath.ParseStats(entity, statsRow, formMods, runes, diff, others);

        return new EnemyStatResult(
            effectiveKey,
            enemyInfo.Names.GetValueOrDefault("zh") ?? enemyInfo.Names.GetValueOrDefault("en"),
            context.StageLevelId,
            difficulty,
            context.Layer,
            values);
    }

    /// <summary>楼层成长：目前支持「每层 hp/atk 各乘 floorBuff^层数」（黑流树海 / 岁的界园志异）。</summary>
    private static ModGroupData? BuildFloorGroup(string topic, int difficulty, int layer, int extraFloorCount)
    {
        if (!ThemeProfiles.TryGet(topic, out var profile) || profile.FloorBuff != FloorBuffKind.ObjectPower)
        {
            return null;
        }

        var entry = IsIntelData
            .GetDifficulty(topic)
            .FirstOrDefault(item => item.Difficulty == difficulty);

        if (entry is null
            || entry.FloorBuff.ValueKind != System.Text.Json.JsonValueKind.Object
            || !entry.FloorBuff.TryGetProperty("hp", out var hpElement)
            || !entry.FloorBuff.TryGetProperty("atk", out var atkElement))
        {
            return null;
        }

        var floor = Math.Max(1, layer) + Math.Max(0, extraFloorCount);
        var hpMultiplier = Math.Pow(hpElement.GetDouble(), floor);
        var atkMultiplier = Math.Pow(atkElement.GetDouble(), floor);

        var effect = new EffectDef
        {
            Targets = new List<string> { "ALL" },
            Mods = new List<ModDef?>
            {
                new() { Key = "hp", Value = hpMultiplier, Mode = "mul", Order = "final" },
                new() { Key = "atk", Value = atkMultiplier, Mode = "mul", Order = "final" }
            }
        };

        return new ModGroupData("floor_diff", new List<List<EffectDef?>?> { new() { effect } });
    }

    /// <summary>
    /// 低难度补偿（tomimi 里手工维护的「新手帮助」）：黑流树海 0-3、岁的界园志异 0-1
    /// 会额外降低敌人生命/攻击。带 BATTLE_SKY 条件的效果因为不知道关卡类型，暂不参与。
    /// </summary>
    private static List<EffectDef?>? BuildLowDifficultyEffects(string topic, int difficulty)
    {
        var effects = new List<EffectDef?>();

        void Add(double hp, double atk)
        {
            effects.Add(new EffectDef
            {
                Targets = new List<string> { "ALL" },
                Mods = new List<ModDef?>
                {
                    new() { Key = "hp", Value = hp, Mode = "mul", Order = "final" },
                    new() { Key = "atk", Value = atk, Mode = "mul", Order = "final" }
                }
            });
        }

        switch (topic)
        {
            case "rogue_6":
                switch (difficulty)
                {
                    case 0: Add(0.6, 0.7); break;
                    case 1: Add(0.8, 0.8); break;
                    case 2: Add(0.85, 0.85); break;
                    case 3: Add(0.9, 0.9); break;
                }

                break;

            case "rogue_yan":
                switch (difficulty)
                {
                    case 0: Add(0.8, 0.8); break;
                    case 1: Add(0.9, 0.9); break;
                }

                break;
        }

        return effects.Count > 0 ? effects : null;
    }
}
