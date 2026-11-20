using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AngelinaISAssistant.Live.Intel;

/// <summary>关卡名别名（由 tomimi.dev 数据生成，见 Live/Intel/data/README.txt）。</summary>
internal sealed class StageNameAlias
{
    public string Text { get; set; } = string.Empty;

    public string Display { get; set; } = string.Empty;

    public string LevelId { get; set; } = string.Empty;

    public string Topic { get; set; } = string.Empty;
}

/// <summary>关卡信息：代码、主题，以及同一关卡的多个变体。</summary>
internal sealed class StageInfo
{
    public string Code { get; set; } = string.Empty;

    public string Topic { get; set; } = string.Empty;

    public List<StageVariant> Variants { get; set; } = new();
}

/// <summary>紧急作战的敌人替换表（正常敌人 key -> 精英版 key）。</summary>
internal sealed class EliteRunes
{
    public Dictionary<string, string> EnemyReplace { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>关卡变体：关卡固有修正、紧急作战替换与敌人列表。</summary>
internal sealed class StageVariant
{
    public string Suffix { get; set; } = string.Empty;

    [JsonPropertyName("all_mods")]
    public List<EffectDef?>? AllMods { get; set; }

    [JsonPropertyName("n_mods")]
    public List<EffectDef?>? NMods { get; set; }

    [JsonPropertyName("elite_mods")]
    public List<EffectDef?>? EliteMods { get; set; }

    /// <summary>紧急作战替换（例如 炮手 -> 炮击组长）；没有则为 null。</summary>
    [JsonPropertyName("elite_runes")]
    public EliteRunes? EliteRunes { get; set; }

    public List<StageEnemyRef> Enemies { get; set; } = new();
}

/// <summary>关卡里出现的一个敌人（level = 敌人数据库 stats 下标）。</summary>
internal sealed class StageEnemyRef
{
    public string Key { get; set; } = string.Empty;

    public int Level { get; set; }

    public string? Id { get; set; }

    public bool Overwritten { get; set; }
}

/// <summary>一条修正效果：targets 决定作用于哪些敌人，mods 是具体数值修正。</summary>
internal sealed class EffectDef
{
    public List<string> Targets { get; set; } = new();

    public List<ModDef?>? Mods { get; set; }

    /// <summary>关卡类型条件（如 BATTLE_SKY）；目前没有关卡类型信息，带条件的修正不参与计算。</summary>
    public List<string>? Conditions { get; set; }

    /// <summary>特殊技能/形态覆盖数据（预留，暂不参与计算）。</summary>
    public JsonElement? Special { get; set; }
}

/// <summary>单条数值修正。</summary>
internal sealed class ModDef
{
    public string Key { get; set; } = string.Empty;

    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double Value { get; set; }

    public string Mode { get; set; } = "mul";

    /// <summary>initial / final；缺省按 final 处理（与 tomimi 一致）。</summary>
    public string? Order { get; set; }

    public string? Name { get; set; }

    /// <summary>rune 标记（来自收藏品时会被路由到关卡修正，预留）。</summary>
    public string? Type { get; set; }
}

/// <summary>敌人模板（只保留计算用到的字段）。</summary>
internal sealed class EnemyInfo
{
    public string? Id { get; set; }

    /// <summary>zh / ja / en 三种语言的敌人名（可能缺省）。</summary>
    public Dictionary<string, string?> Names { get; set; } = new();

    /// <summary>ELITE / BOSS / melee / ranged / flying 等类型标记。</summary>
    public List<string> Type { get; set; } = new();

    public List<EnemyStatsRow> Stats { get; set; } = new();
}

/// <summary>敌人在某一等级下的基础数值（对应 enemy_database.json 的 stats[level]）。</summary>
internal sealed class EnemyStatsRow
{
    public double Hp { get; set; }

    public double Atk { get; set; }

    public double Aspd { get; set; }

    public double Range { get; set; }

    public double Def { get; set; }

    public double Res { get; set; }

    public double Weight { get; set; }

    public double Ms { get; set; }

    public double Lifepoint { get; set; }

    public double EpRes { get; set; }

    public double EpDmg { get; set; }

    public List<string?> Traits { get; set; } = new();

    /// <summary>各形态的固有数值修正（form_mods[form] = Mod[]）。</summary>
    public List<List<ModDef?>?>? FormMods { get; set; }
}

/// <summary>某个主题某一难度下的修正与楼层成长。</summary>
internal sealed class DifficultyEntry
{
    public int Difficulty { get; set; }

    /// <summary>楼层成长：黑流树海/岁的界园志异是 {hp, atk} 对象。</summary>
    public JsonElement FloorBuff { get; set; }

    public List<EffectDef?>? Effects { get; set; }
}
