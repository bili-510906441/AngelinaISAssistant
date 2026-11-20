using System;
using System.Collections.Generic;
using System.Linq;

namespace AngelinaISAssistant.Live.Intel;

/// <summary>编译后的一条数值修正（同 key/order/mode 已按 tomimi 规则合并）。</summary>
internal sealed class CompiledMod
{
    public string Key { get; init; } = string.Empty;

    public string Mode { get; init; } = "mul";

    public string Order { get; init; } = "final";

    public string Name { get; init; } = string.Empty;

    public double Value { get; set; }
}

/// <summary>编译后的修正组；Mods 为空表示这一组没有命中该敌人。</summary>
internal sealed class CompiledModGroup
{
    public string Key { get; init; } = string.Empty;

    public List<CompiledMod> Mods { get; } = new();
}

/// <summary>参与数值计算的修正组定义（对应 tomimi 的 ModGroup）。</summary>
internal sealed class ModGroupData
{
    public ModGroupData(string key, IReadOnlyList<List<EffectDef?>?> groups, string stackType = "mul")
    {
        Key = key;
        Groups = groups;
        StackType = stackType;
    }

    public string Key { get; }

    /// <summary>每组是一整段 Effects（数组元素为 EffectDef，可能为 null）。</summary>
    public IReadOnlyList<List<EffectDef?>?> Groups { get; }

    /// <summary>同一组内多条 mul 修正的叠加方式：mul（相乘）或 add（增量相加）。</summary>
    public string StackType { get; }
}

/// <summary>参与数值计算的目标实体。</summary>
internal sealed class StatEntity
{
    public string Key { get; init; } = string.Empty;

    public string Id { get; init; } = string.Empty;

    /// <summary>关卡内该敌人的别名（对应 tomimi 的 stageId）。</summary>
    public string? StageId { get; init; }

    public IReadOnlyList<string> Type { get; init; } = Array.Empty<string>();
}

/// <summary>敌人某一形态计算后的数值。</summary>
internal sealed record EnemyStatValues(
    double Hp,
    double Atk,
    double Def,
    double Res,
    double Ms,
    double Aspd,
    double Weight,
    double EpRes,
    double EpDmg);

/// <summary>
/// tomimi.dev 敌人数值叠加公式的 C# 实现（与 src/lib/functions/statHelpers.ts 对齐）：
/// 关卡修正（runes）先取整，再叠加形态 / 难度 / 楼层成长 / 收藏品等 secondary 修正。
/// </summary>
internal static class StatMath
{
    // ---- 与 tomimi 完全一致的特例清单 ----
    private static readonly HashSet<string> PhcsBosses = new(StringComparer.Ordinal)
    {
        "ICR", "ICM", "MSC", "FSK", "JTM", "WDG", "PTM", "DOL", "HST", "WRT"
    };

    private static readonly HashSet<string> MzkBosses = new(StringComparer.Ordinal)
    {
        "enemy_2026_syudg", "enemy_2027_syudg2", "enemy_2028_syevil", "enemy_2029_symon",
        "enemy_2030_symon2", "enemy_2031_syboy", "enemy_2032_syboy2", "enemy_2036_syshop",
        "enemy_2037_sygirl", "enemy_2038_sydonq", "enemy_2039_syskad", "enemy_2006_flsnip",
        "enemy_2007_flwitch", "enemy_2008_flking"
    };

    private static readonly HashSet<string> SamiBosses = new(StringComparer.Ordinal)
    {
        "enemy_2048_smgrd", "enemy_2049_smgrd2", "enemy_2050_smsha", "enemy_2051_smsha2",
        "enemy_2052_smgia", "enemy_2053_smgia2", "enemy_2054_smdeer", "enemy_2055_smlead",
        "enemy_2056_smedzi", "enemy_2036_syshop", "enemy_2057_smkght", "enemy_2058_smlion"
    };

    private static readonly HashSet<string> SarkazBosses = new(StringComparer.Ordinal)
    {
        "enemy_2074_skzgly", "enemy_2075_skgly2", "enemy_2076_skzklz", "enemy_2077_skklz2",
        "enemy_2078_skzmst", "enemy_2079_skmst2", "enemy_2080_skzlwy", "enemy_2081_skztxs",
        "enemy_2082_skzdd", "enemy_2084_skzcan", "enemy_2071_skzdny", "enemy_2072_skdny2"
    };

    private static readonly HashSet<string> NeutralEnemies = new(StringComparer.Ordinal)
    {
        "enemy_3001_upeopl", "enemy_10061_cjglon"
    };

    private static readonly HashSet<string> NotAffectedByFloorDiffKeys = new(StringComparer.Ordinal)
    {
        "enemy_10062_cjblon"
    };

    private static readonly HashSet<string> Damage1AtkVariants = new(StringComparer.Ordinal)
    {
        "enemy_2121_dyspl2"
    };

    private static readonly string[] StatKeys =
    {
        "hp", "atk", "aspd", "range", "def", "res", "weight", "ms", "lifepoint", "epRes", "epDmg"
    };

    /// <summary>JS 的 Math.round（四舍五入，.5 向 +∞）；C# Math.Round 是银行家舍入，不能直接用。</summary>
    public static double RoundJs(double value) => Math.Floor(value + 0.5);

    /// <summary>对应 numberHelpers.round(value, decimalPoints)：默认 2 位小数。</summary>
    public static double RoundJs(double value, int decimalPoints)
    {
        var multiplier = Math.Pow(10, decimalPoints);
        return RoundJs(value * multiplier) / multiplier;
    }

    /// <summary>把一组修正效果编译成「同 key/order/mode 合并」的修正列表（compileMods）。</summary>
    public static CompiledModGroup CompileMods(StatEntity entity, ModGroupData group, string type = "enemy")
    {
        var compiled = new CompiledModGroup { Key = group.Key };
        var stackType = group.StackType;

        foreach (var effects in group.Groups)
        {
            if (effects is null)
            {
                continue;
            }

            foreach (var effect in effects)
            {
                if (effect?.Mods is null || effect.Mods.Count == 0)
                {
                    continue;
                }

                if (type is "trap_rune" or "neutral" or "damage_1_enemy"
                    && !effect.Targets.Any(target => target.Contains(entity.Key, StringComparison.Ordinal)))
                {
                    continue;
                }

                // 带关卡类型条件的效果需要知道 stageType；目前没有该信息，按「不生效」处理
                if (effect.Conditions is { Count: > 0 })
                {
                    continue;
                }

                if (!effect.Targets.Any(target => CheckIsTarget(entity, target)))
                {
                    continue;
                }

                foreach (var mod in effect.Mods)
                {
                    if (mod is null)
                    {
                        continue;
                    }

                    var mode = string.IsNullOrEmpty(mod.Mode) ? "mul" : mod.Mode;
                    var order = string.IsNullOrEmpty(mod.Order) ? "final" : mod.Order;
                    var name = mod.Name ?? string.Empty;

                    if (type == "damage_1_atk_variant" && mod.Key == "hp")
                    {
                        if (group.Key == "floor_diff")
                        {
                            continue;
                        }

                        if (group.Key == "difficulty"
                            && !effect.Targets.Any(target => target.Contains(entity.Key, StringComparison.Ordinal)))
                        {
                            continue;
                        }
                    }

                    var item = compiled.Mods.FirstOrDefault(existing =>
                        existing.Key == mod.Key && existing.Order == order && existing.Mode == mode);

                    if (item is null)
                    {
                        compiled.Mods.Add(new CompiledMod
                        {
                            Key = mod.Key,
                            Mode = mode,
                            Order = order,
                            Name = name,
                            Value = mod.Value
                        });
                        continue;
                    }

                    if (mod.Key == "dmg_res")
                    {
                        if (item.Name.Length > 0)
                        {
                            if (mod.Value > item.Value)
                            {
                                item.Value = mod.Value;
                            }

                            continue;
                        }

                        item.Value += mod.Value;
                        continue;
                    }

                    switch (mode)
                    {
                        case "mul":
                            if (stackType == "mul")
                            {
                                item.Value *= mod.Value;
                            }
                            else
                            {
                                item.Value += mod.Value - 1;
                            }

                            break;

                        case "add":
                            item.Value += mod.Value;
                            break;
                    }
                }
            }
        }

        return compiled;
    }

    /// <summary>聚合所有修正得到最终属性（getModdedStat）。</summary>
    public static double GetModdedStat(double baseValue, string statKey, params CompiledModGroup?[] groups)
    {
        var initialAdd = 0d;
        var initialMul = 1d;
        var finalAdd = 0d;
        var finalMul = 1d;
        var atkIntervalAdd = 0d;
        var atkIntervalMul = 1d;

        foreach (var group in groups)
        {
            if (group is null)
            {
                continue;
            }

            foreach (var mod in group.Mods)
            {
                if (mod.Key == "atk_interval")
                {
                    switch (mod.Mode)
                    {
                        case "mul":
                            atkIntervalMul *= mod.Value;
                            break;
                        case "add":
                            atkIntervalAdd += mod.Value;
                            break;
                    }

                    continue;
                }

                if (mod.Key != statKey)
                {
                    continue;
                }

                switch (mod.Mode)
                {
                    case "mul":
                        if (mod.Order == "initial")
                        {
                            initialMul += mod.Value;
                        }
                        else
                        {
                            finalMul *= mod.Value;
                        }

                        break;

                    case "add":
                        if (mod.Order == "initial")
                        {
                            initialAdd += mod.Value;
                        }
                        else
                        {
                            finalAdd += mod.Value;
                        }

                        break;
                }
            }
        }

        return CalculateModdedStat(baseValue, statKey, initialAdd, initialMul, finalAdd, finalMul, atkIntervalAdd, atkIntervalMul);
    }

    /// <summary>对应 calculateModdedStat：逐属性的取整与 clamp 规则。</summary>
    public static double CalculateModdedStat(
        double baseValue,
        string statKey,
        double initialAdd,
        double initialMul,
        double finalAdd,
        double finalMul,
        double atkIntervalAdd,
        double atkIntervalMul)
    {
        const double baseAspd = 100;

        if (statKey != "aspd")
        {
            baseValue += initialAdd;
        }

        switch (statKey)
        {
            case "aspd":
                // aspd 保留 2 位小数（tomimi 的 round 默认 2 位）
                return RoundJs(
                    (baseValue + atkIntervalAdd) * atkIntervalMul
                    / (Math.Max(10, Math.Min(((baseAspd + initialAdd) * initialMul + finalAdd) * finalMul, 600)) / 100),
                    2);

            case "range":
            case "ms":
                return RoundJs((baseValue * initialMul + finalAdd) * finalMul, 2);

            case "weight":
            case "res":
                return Math.Round(Math.Min(Math.Max((baseValue * initialMul + finalAdd) * finalMul, 0), 100));

            case "hp":
            {
                var value = (baseValue * initialMul + finalAdd) * finalMul;

                return value < 100 ? RoundJs(value, 1) : Math.Round(value);
            }

            default:
                return Math.Round(Math.Max(0, (baseValue * initialMul + finalAdd) * finalMul));
        }
    }

    /// <summary>减伤（getDmgReductionVal）：同名取最大，其余按 1-(1-a)(1-b) 叠加。</summary>
    public static double GetDmgReductionVal(params CompiledModGroup?[] groups)
    {
        var namedValues = new List<(string Name, double Value)>();
        var otherValues = new List<double>();

        foreach (var group in groups)
        {
            if (group is null)
            {
                continue;
            }

            foreach (var mod in group.Mods)
            {
                if (mod.Key != "dmg_res")
                {
                    continue;
                }

                if (mod.Name.Length > 0)
                {
                    var index = namedValues.FindIndex(item => item.Name == mod.Name);

                    if (index < 0)
                    {
                        namedValues.Add((mod.Name, mod.Value));
                    }
                    else if (mod.Value > namedValues[index].Value)
                    {
                        namedValues[index] = (mod.Name, mod.Value);
                    }

                    continue;
                }

                otherValues.Add(mod.Value);
            }
        }

        var accumulator = 0d;

        foreach (var value in namedValues.Select(item => item.Value).Concat(otherValues))
        {
            accumulator = accumulator == 0 ? value : 1 - (1 - accumulator) * (1 - value);
        }

        return RoundJs(accumulator);
    }

    /// <summary>完整复刻 parseStats：先算关卡修正并取整，再叠加形态/难度/其它修正。</summary>
    public static EnemyStatValues ParseStats(
        StatEntity entity,
        EnemyStatsRow stats,
        List<ModDef?>? formMods,
        ModGroupData runes,
        ModGroupData? diff,
        IReadOnlyList<ModGroupData> others)
    {
        var type = ClassifyType(entity, stats);
        var diffCompiled = diff is null ? null : CompileMods(entity, diff, type);

        var formGroup = new CompiledModGroup { Key = "form_mods" };

        if (formMods is not null)
        {
            foreach (var mod in formMods)
            {
                if (mod is null)
                {
                    continue;
                }

                formGroup.Mods.Add(new CompiledMod
                {
                    Key = mod.Key,
                    Mode = string.IsNullOrEmpty(mod.Mode) ? "mul" : mod.Mode,
                    Order = string.IsNullOrEmpty(mod.Order) ? "final" : mod.Order,
                    Name = mod.Name ?? string.Empty,
                    Value = mod.Value
                });
            }
        }

        var runeCompiled = CompileMods(entity, runes);
        var otherCompiled = others
            .Select(group => CompileMods(entity, group, type == "damage_1_atk_variant" ? type : "enemy"))
            .ToList();

        var secondary = new List<CompiledModGroup> { formGroup };

        if (diffCompiled is not null)
        {
            secondary.Add(diffCompiled);
        }

        secondary.AddRange(otherCompiled);

        if (NeutralEnemies.Contains(entity.Key)
            || type == "damage_1_enemy"
            || NotAffectedByFloorDiffKeys.Contains(entity.Key))
        {
            secondary = secondary.Where(group => group.Key != "floor_diff").ToList();
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var statKey in StatKeys)
        {
            var initialValue = GetBaseStat(stats, statKey);
            var isSet = false;

            if (formMods is not null)
            {
                var setMod = formMods.FirstOrDefault(mod =>
                    mod is not null && mod.Key == statKey && mod.Mode == "set");

                if (setMod is not null)
                {
                    initialValue = setMod.Value;
                    isSet = true;
                }
            }

            if (statKey == "aspd")
            {
                values[statKey] = GetModdedStat(
                    GetBaseStat(stats, "aspd"),
                    "aspd",
                    new[] { runeCompiled }.Concat(secondary).ToArray());
                continue;
            }

            var baseValue = isSet
                ? initialValue
                : RoundJs(
                    GetModdedStat(initialValue, statKey, runeCompiled),
                    statKey is "ms" or "range" ? 2 : 0);

            values[statKey] = GetModdedStat(baseValue, statKey, secondary.ToArray());
        }

        return new EnemyStatValues(
            values["hp"],
            values["atk"],
            values["def"],
            values["res"],
            values["ms"],
            values["aspd"],
            values["weight"],
            values["epRes"],
            values["epDmg"]);
    }

    /// <summary>对应 checkIsTarget：targets 字符串的匹配规则。</summary>
    public static bool CheckIsTarget(StatEntity entity, string target)
    {
        if (target.Contains("exclude:", StringComparison.Ordinal))
        {
            var list = target.Replace("exclude:", string.Empty, StringComparison.Ordinal).Split('|');

            return !list.Contains(entity.Key, StringComparer.Ordinal);
        }

        if (target.Contains('&'))
        {
            return target.Split('&').All(part => CheckIsTarget(entity, part));
        }

        switch (target)
        {
            case "ALL":
                return true;

            case "ranged":
            case "melee":
            case "ELITE":
            case "BOSS":
            case "collapsal":
            case "sarkaz":
            case "animated":
            case "mutant":
                return entity.Type.Contains(target, StringComparer.Ordinal);

            case "PHCS_BOSS":
                return PhcsBosses.Contains(entity.Id);

            case "NOT_PHCS_BOSS":
                return !PhcsBosses.Contains(entity.Id);

            case "MZK_BOSS":
                return MzkBosses.Contains(entity.Key);

            case "NOT_MZK_BOSS":
                return !MzkBosses.Contains(entity.Key);

            case "SAMI_BOSS":
                return SamiBosses.Contains(entity.Key);

            case "NOT_SAMI_BOSS":
                return !SamiBosses.Contains(entity.Key);

            case "SARKAZ_BOSS":
                return SarkazBosses.Contains(entity.Key);

            case "NOT_SARKAZ_BOSS":
                return !SarkazBosses.Contains(entity.Key);

            case "not_flying":
                return !entity.Type.Contains("flying", StringComparer.Ordinal);

            case "not_trap":
                return !entity.Key.Contains("trap", StringComparison.Ordinal);

            default:
                return entity.Id == target || entity.Key == target || entity.StageId == target;
        }
    }

    private static string ClassifyType(StatEntity entity, EnemyStatsRow stats)
    {
        if (NeutralEnemies.Contains(entity.Key))
        {
            return "neutral";
        }

        if (Damage1AtkVariants.Contains(entity.Key))
        {
            return "damage_1_atk_variant";
        }

        var isDamage1 = stats.Traits.Any(trait => trait is "damage_1" or "damage_1_arts")
                        && entity.Key != "enemy_2122_dybgzd";

        return isDamage1 ? "damage_1_enemy" : "enemy";
    }

    private static double GetBaseStat(EnemyStatsRow stats, string statKey) => statKey switch
    {
        "hp" => stats.Hp,
        "atk" => stats.Atk,
        "aspd" => stats.Aspd,
        "range" => stats.Range,
        "def" => stats.Def,
        "res" => stats.Res,
        "weight" => stats.Weight,
        "ms" => stats.Ms,
        "lifepoint" => stats.Lifepoint,
        "epRes" => stats.EpRes,
        "epDmg" => stats.EpDmg,
        _ => 0
    };
}
