#!/usr/bin/env python3
"""从 tomimi.dev 的集成战略数据生成「关卡名 / 敌人 / 难度」C# 内嵌数据。

数据源（开发侧单独保留的 tomimi-dev 仓库）：
  src/lib/data/stages/ro1.json ... ro6.json         关卡列表（关卡名 / 代码 / levelId）
  src/lib/data/stages/stage_name_lookup_table.json  三种语言的关卡名（含代码前缀）
  src/lib/data/stages/stage_name_overwrite_table.json 变体关卡名称覆盖
  src/lib/data/stages/ro_stage_data/roN/*.json      关卡数据（n_mods / all_mods / elite_mods / 敌人列表）
  src/lib/data/enemy/enemy_database.json            敌人模板与各形态数值
  src/lib/data/is/<topic>/difficulty_mods*.json     主题难度修正与楼层成长

输出到 Live/Intel/data/：
  stage_names.json  关卡名别名（识别用，支持中/英/日）
  stages.json       关卡代码 / 主题 / 变体修正 / 敌人列表
  enemies.json      被关卡引用到的敌人模板（名字 / 类型 / 各等级数值 / form_mods）
  difficulty.json   各主题难度修正（difficulty / floorBuff / effects）

用法：
  python version_tools/generate_intel_data.py
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys

RO_FILES = {
    "ro1": "rogue_phantom",
    "ro2": "rogue_mizuki",
    "ro3": "rogue_sami",
    "ro4": "rogue_skz",
    "ro5": "rogue_yan",
    "ro6": "rogue_6",
}

TOPIC_TITLES = {
    "rogue_phantom": "傀影与猩红孤钻",
    "rogue_mizuki": "水月与深蓝之树",
    "rogue_sami": "探索者的银凇止境",
    "rogue_skz": "萨卡兹的无终奇语",
    "rogue_yan": "岁的界园志异",
    "rogue_6": "沉沦者的黑流树海",
}

DIFF_FILES = {
    "rogue_phantom": ("phantom", "difficulty_mods_phantom.json"),
    "rogue_mizuki": ("mizuki", "difficulty_mods_mizuki.json"),
    "rogue_sami": ("sami", "difficulty_mods_sami.json"),
    "rogue_skz": ("sarkaz", "difficulty_mods_sarkaz.json"),
    "rogue_yan": ("sui", "difficulty_mods_sui.json"),
    "rogue_6": ("black", "difficulty_mods.json"),
}

# 关卡名里的变体后缀：「(单)」「（双）」以及数据后缀「_b」「_dlc1」
VARIANT_PAREN = re.compile(r"\s*[\(（]\s*(?:单|双|1|2)\s*[\)）]\s*$")
VARIANT_DATA = re.compile(r"[_\-\s]+(?:b|c|d|e|dlc\d*)\s*$", re.IGNORECASE)


def load_json(path: str):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def clean_name(name: str, lang: str) -> str:
    """展示名：英文下划线变空格，去掉变体后缀。"""
    text = name.strip()

    if lang == "en":
        text = text.replace("_", " ")

    while True:
        stripped = VARIANT_DATA.sub("", text)
        stripped = VARIANT_PAREN.sub("", stripped)

        if stripped == text:
            break

        text = stripped

    return re.sub(r"\s+", " ", text).strip()


def add_alias(sink: dict, level_id: str, topic: str, lang: str, raw: str) -> None:
    if not raw:
        return

    display = clean_name(raw, lang)

    if not display:
        return

    key = (level_id, lang)

    # 同一个 levelId 的多条别名（原始名 / 去掉变体后缀的名字）都保留
    bucket = sink.setdefault(key, {"raw": set(), "display": set()})
    bucket["raw"].add(raw.strip())
    bucket["display"].add(display)


def collect(tomimi_root: str, out_dir: str) -> dict:
    stages_dir = os.path.join(tomimi_root, "src", "lib", "data", "stages")
    enemy_db_path = os.path.join(tomimi_root, "src", "lib", "data", "enemy", "enemy_database.json")
    is_dir = os.path.join(tomimi_root, "src", "lib", "data", "is")

    if not os.path.isdir(stages_dir):
        raise SystemExit(f"找不到 tomimi 数据目录：{stages_dir}")

    # ---- 1. levelId -> 基本信息（代码 / 主题） + 别名 ----
    levels: dict[str, dict] = {}
    aliases: dict[tuple[str, str], dict] = {}

    for ro_file, topic in RO_FILES.items():
        data = load_json(os.path.join(stages_dir, ro_file + ".json"))

        for key, entry in data.items():
            level_id = entry.get("levelId") or key
            meta = levels.setdefault(level_id, {"topic": topic, "code": entry.get("code")})

            if meta.get("code") is None:
                meta["code"] = entry.get("code")

            for lang in ("zh", "ja", "en"):
                add_alias(aliases, level_id, topic, lang, entry.get("name_" + lang) or "")

    # 名称覆盖表：部分变体关卡（例如 谤天(单)/谤天(双)）的真实名称
    overwrite = load_json(os.path.join(stages_dir, "stage_name_overwrite_table.json"))

    for level_id, entry in overwrite.items():
        for lang in ("zh", "ja", "en"):
            add_alias(aliases, level_id, levels.get(level_id, {}).get("topic", ""), lang, entry.get("name_" + lang) or "")

    # 三语言关卡名查找表（key = 代码_名字）
    lookup = load_json(os.path.join(stages_dir, "stage_name_lookup_table.json"))

    for key, entry in lookup.items():
        lang = entry.get("lang")
        level_id = entry.get("key")

        if lang not in ("zh", "ja", "en") or not level_id:
            continue

        name = key.split("_", 1)[1] if "_" in key else key
        levels.setdefault(level_id, {"topic": entry.get("topic"), "code": None})
        add_alias(aliases, level_id, entry.get("topic") or "", lang, name)

    # ---- 2. 关卡变体数据：修正 / 敌人列表 ----
    variant_data: dict[str, list] = {}
    enemy_keys: set[str] = set()
    stage_files = 0
    missing_files: list[str] = []

    for level_id, meta in levels.items():
        folder = None

        for ro_file, topic in RO_FILES.items():
            if topic == meta["topic"]:
                folder = ro_file
                break

        if folder is None:
            missing_files.append(level_id)
            continue

        path = os.path.join(stages_dir, "ro_stage_data", folder, level_id + ".json")

        if not os.path.exists(path):
            missing_files.append(level_id)
            continue

        stage_files += 1
        raw = load_json(path)
        variants = []

        for item in raw.get("data") or []:
            enemies = []

            for enemy in item.get("enemies") or []:
                key = enemy.get("prefabKey")

                if not key:
                    continue

                enemy_keys.add(key)
                enemies.append(
                    {
                        "key": key,
                        "level": enemy.get("level") or 0,
                        "id": enemy.get("id"),
                        "overwritten": bool(enemy.get("overwrittenData")),
                    }
                )

            # 紧急作战会替换部分敌人（elite_runes.enemy_replace，或 elite_mods 里的 level_enemy_replace）
            enemy_replace: dict[str, str] = {}

            elite_runes = item.get("elite_runes") or {}
            for source, target in (elite_runes.get("enemy_replace") or {}).items():
                if source and target:
                    enemy_replace[source] = target

            for effects in item.get("elite_mods") or []:
                for mod in (effects or {}).get("mods") or []:
                    if mod.get("key") == "level_enemy_replace" and mod.get("target") and mod.get("value"):
                        enemy_replace[mod["target"]] = mod["value"]

            for target in enemy_replace.values():
                enemy_keys.add(target)

            variant = {
                "suffix": item.get("suffix") or "",
                "all_mods": item.get("all_mods"),
                "n_mods": item.get("n_mods"),
                "elite_mods": item.get("elite_mods"),
                "elite_runes": {"enemyReplace": enemy_replace} if enemy_replace else None,
                "enemies": enemies,
            }
            variants.append(variant)

            for lang in ("zh", "ja", "en"):
                add_alias(aliases, level_id, meta["topic"], lang, item.get("name_" + lang) or "")

        variant_data[level_id] = variants

    # ---- 3. 敌人模板（只保留被关卡引用到的） ----
    enemy_db = load_json(enemy_db_path)
    enemies: dict[str, dict] = {}

    for key in sorted(enemy_keys):
        entry = enemy_db.get(key)

        if entry is None:
            continue

        stats = []

        for stat in entry.get("stats") or []:
            stats.append(
                {
                    "hp": stat.get("hp"),
                    "atk": stat.get("atk"),
                    "aspd": stat.get("aspd"),
                    "range": stat.get("range"),
                    "def": stat.get("def"),
                    "res": stat.get("res"),
                    "weight": stat.get("weight"),
                    "ms": stat.get("ms"),
                    "lifepoint": stat.get("lifepoint"),
                    "epRes": stat.get("epResistance"),
                    "epDmg": stat.get("epDamageResistance"),
                    "traits": [trait.get("key") for trait in (stat.get("traits") or [])],
                    "formMods": stat.get("form_mods"),
                }
            )

        enemies[key] = {
            "id": entry.get("id"),
            "names": {lang: entry.get("name_" + lang) for lang in ("zh", "ja", "en")},
            "type": entry.get("type") or [],
            "stats": stats,
        }

    # ---- 4. 难度修正 ----
    difficulty: dict[str, list] = {}

    for topic, (folder, file_name) in DIFF_FILES.items():
        path = os.path.join(is_dir, folder, file_name)

        if not os.path.exists(path):
            continue

        entries = []

        for entry in load_json(path):
            effects = entry.get("effects") or []
            entries.append(
                {
                    "difficulty": entry.get("difficulty"),
                    "floorBuff": entry.get("floorBuff"),
                    "effects": effects if effects else None,
                }
            )

        difficulty[topic] = entries

    # ---- 5. 序列化 ----
    stage_names_out = []
    name_index: dict[tuple[str, str], dict] = {}

    for (level_id, lang), bucket in aliases.items():
        level_meta = levels.get(level_id)

        if level_meta is None:
            continue

        for display in sorted(bucket["display"]):
            for raw in sorted(bucket["raw"]):
                record = name_index.setdefault(
                    (raw, level_id),
                    {
                        "text": raw,
                        "display": display,
                        "levelId": level_id,
                        "topic": level_meta["topic"],
                    },
                )
                # 若同一 raw 有多个 display（极少），保留最短的
                if len(display) < len(record["display"]):
                    record["display"] = display

    stage_names_out = sorted(name_index.values(), key=lambda item: (item["levelId"], item["text"]))

    stages_out = {}

    for level_id, meta in levels.items():
        code = meta.get("code")

        if code is None:
            # 从别的来源补代码：查找表的值里没有 code，跳过也可以；用 ISW-NO 兜底不合适
            code = ""

        stages_out[level_id] = {
            "code": code,
            "topic": meta["topic"],
            "variants": variant_data.get(level_id) or [],
        }

    data = {
        "stage_names": stage_names_out,
        "stages": stages_out,
        "enemies": enemies,
        "difficulty": difficulty,
    }

    os.makedirs(out_dir, exist_ok=True)

    def dump(name: str, value) -> int:
        raw = json.dumps(value, ensure_ascii=False, separators=(",", ":"))
        path = os.path.join(out_dir, name)
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(raw)

        return len(raw.encode("utf-8"))

    sizes = {
        "stage_names.json": dump("stage_names.json", stage_names_out),
        "stages.json": dump("stages.json", stages_out),
        "enemies.json": dump("enemies.json", enemies),
        "difficulty.json": dump("difficulty.json", difficulty),
    }

    readme = os.path.join(out_dir, "README.txt")

    with open(readme, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(
            "本目录的 JSON 由 version_tools/generate_intel_data.py 从 tomimi.dev 的集成战略数据生成，请勿手改。\n"
            "\n"
            "数据来源（开发侧单独保留的 tomimi-dev 仓库）：\n"
            "  src/lib/data/stages/ro1.json ... ro6.json\n"
            "  src/lib/data/stages/stage_name_lookup_table.json\n"
            "  src/lib/data/stages/stage_name_overwrite_table.json\n"
            "  src/lib/data/stages/ro_stage_data/roN/*.json\n"
            "  src/lib/data/enemy/enemy_database.json\n"
            "  src/lib/data/is/<topic>/difficulty_mods*.json\n"
            "\n"
            "重新生成（在 code 目录下）：\n"
            "  python version_tools/generate_intel_data.py\n"
        )

    return {
        "levels": len(levels),
        "stage_files": stage_files,
        "missing_files": missing_files,
        "aliases": len(stage_names_out),
        "enemies": len(enemies),
        "topics": sorted(difficulty),
        "sizes": sizes,
    }


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    default_tomimi = os.path.normpath(
        os.path.join(here, "..", "..", "user_resources", "tomimi-dev-main", "tomimi-dev-main")
    )
    default_out = os.path.normpath(os.path.join(here, "..", "Live", "Intel", "data"))

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tomimi", default=default_tomimi, help="tomimi-dev-main 仓库根目录")
    parser.add_argument("--out", default=default_out, help="输出目录（Live/Intel/data）")
    args = parser.parse_args()

    summary = collect(args.tomimi, args.out)

    print(f"已生成到 {args.out}")
    print(f"  关卡：{summary['levels']} 个（读到关卡数据 {summary['stage_files']} 个）")
    print(f"  关卡名别名：{summary['aliases']} 条")
    print(f"  敌人模板：{summary['enemies']} 个")
    print(f"  难度数据主题：{', '.join(summary['topics'])}")

    if summary["missing_files"]:
        print(f"  缺少关卡数据文件的 levelId：{len(summary['missing_files'])} 个（已跳过）")

        for level_id in summary["missing_files"][:10]:
            print(f"    - {level_id}")

    for name, size in summary["sizes"].items():
        print(f"  {name}: {size / 1024:.1f} KB")

    return 0


if __name__ == "__main__":
    sys.exit(main())
