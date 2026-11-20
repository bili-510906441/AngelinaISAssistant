本目录的 JSON 由 version_tools/generate_intel_data.py 从 tomimi.dev 的集成战略数据生成，请勿手改。

数据来源（开发侧单独保留的 tomimi-dev 仓库）：
  src/lib/data/stages/ro1.json ... ro6.json
  src/lib/data/stages/stage_name_lookup_table.json
  src/lib/data/stages/stage_name_overwrite_table.json
  src/lib/data/stages/ro_stage_data/roN/*.json
  src/lib/data/enemy/enemy_database.json
  src/lib/data/is/<topic>/difficulty_mods*.json

重新生成（在 code 目录下）：
  python version_tools/generate_intel_data.py
