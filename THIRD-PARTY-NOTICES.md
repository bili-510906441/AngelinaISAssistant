# 第三方组件与素材声明

本项目以 **GNU Affero General Public License v3.0（AGPL-3.0）** 发布，完整许可证全文见同目录的 `LICENSE`。
以下第三方组件与素材按各自的许可证使用。

---

## 2. HDiffPatch

- 项目地址：https://github.com/sisong/HDiffPatch
- 版本：v5.1.3（windows64 构建）
- 版权：Copyright (c) 2012-2025 housisong
- 许可证：MIT License，全文见 `tools/hdiffpatch/LICENSE.txt`
- 本项目使用范围：随程序分发的 `hpatchz.exe`，用于应用增量更新包；`hdiffz.exe`（生成补丁）只在开发侧的 `new_structure\version_tools` 中使用，不随发布包分发

## 3. Serilog

- 项目地址：https://github.com/serilog/serilog
- 组件：`Serilog`、`Serilog.Sinks.File`
- 版权：Copyright (c) Serilog Contributors
- 许可证：Apache License 2.0
- 本项目使用范围：运行时日志记录与按天滚动的日志文件输出

## 1. Vortice.Windows

- 项目地址：https://github.com/amerkoleci/Vortice.Windows
- 组件：`Vortice.Direct3D11`、`Vortice.DXGI`、`Vortice.DirectX`、`Vortice.Mathematics`、`SharpGen.Runtime`
- 许可证：MIT License
- 本项目使用范围：DXGI Desktop Duplication 画面采集后端

## 4. 源代码

AGPL-3.0 要求分发二进制时向使用者提供对应源代码。本项目源代码地址：

https://github.com/bili-510906441/AngelinaISAssistant

## 5. ONNX Runtime

- 文件：`externals/onnxruntime.dll`、`onnxruntime_providers_shared.dll`
- 版本：1.32.0（win-x64，CPU）
- 许可：MIT，Copyright (c) Microsoft Corporation
- 来源：https://github.com/microsoft/onnxruntime/releases
- 分发时随附 `externals/onnxruntime_LICENSE.txt`

## 6. PaddleOCR PP-OCRv6_small 模型

- 文件：`models/ppocr/PP-OCRv6_small_det.onnx`、`models/ppocr/PP-OCRv6_small_rec.onnx`、`models/ppocr/keys.txt`
- 来源：PaddleOCR / PaddleX 发布的 PP-OCRv6_small 中英文检测与识别模型，经 paddle2onnx 导出为 ONNX（FP32）
- 项目地址：https://github.com/PaddlePaddle/PaddleOCR
- 许可：Apache License 2.0
- 本项目使用范围：实时辅助的文字检测与识别（det → crop → rec），随发布包分发
- 模型文件哈希与转换细节见 `models/ppocr/README.txt`

## 7. ONNX Runtime DirectML 原生库

- 文件：`externals/onnxruntime_dml/onnxruntime.dll`、`onnxruntime_providers_shared.dll`
- 版本：1.24.4（win-x64，DirectML 构建）
- 许可：MIT，Copyright (c) Microsoft Corporation
- 来源：https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.DirectML
- 分发时随附 `externals/onnxruntime_dml/ORT-LICENSE.txt`

## 8. Microsoft DirectML

- 文件：`externals/onnxruntime_dml/DirectML.dll`
- 版本：1.15.4（win-x64）
- 版权：Copyright (c) Microsoft Corporation
- 许可：Microsoft DirectML 许可条款（随附 `externals/onnxruntime_dml/DirectML-LICENSE.txt`、
  `DirectML-ThirdPartyNotices.txt`）
- 来源：https://www.nuget.org/packages/Microsoft.AI.DirectML
- 说明：DirectML 是 Windows 的 D3D12 推理后端，不需要 CUDA/cuDNN。

## 9. 集成战略关卡 / 敌人数据

- 文件：`Live/Intel/data/*.json`（`stage_names.json`、`stages.json`、`enemies.json`、
  `difficulty.json`），由 `version_tools/generate_intel_data.py` 生成并编译进程序
- 来源：tomimi.dev 项目整理的游戏内集成战略数据
  （`src/lib/data/stages/ro1.json ... ro6.json`、`stage_name_lookup_table.json`、
  `stage_name_overwrite_table.json`、`ro_stage_data/roN/*.json`、
  `src/lib/data/enemy/enemy_database.json`、`src/lib/data/is/<topic>/difficulty_mods*.json`）；
  开发侧完整仓库保留在 `new_structure/user_resources/tomimi-dev-main`
- 版权：关卡名称、敌人数据与相关游戏数据版权归上海鹰角网络科技有限公司（Hypergryph）所有
- 本项目使用范围：在悬浮遮罩上提示「识别到{关卡名}」，并在敌方情报页按当前难度/层数计算、
  显示敌人数值；不随发布包分发 tomimi.dev 的源码或原始关卡数据文件