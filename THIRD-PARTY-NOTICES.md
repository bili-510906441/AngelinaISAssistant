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

- 文件：`externals/onnxruntime/onnxruntime.dll`、`onnxruntime_providers_shared.dll`
- 版本：1.32.0（win-x64，CPU）
- 许可：MIT，Copyright (c) Microsoft Corporation
- 来源：https://github.com/microsoft/onnxruntime/releases
- 分发时随附 `externals/onnxruntime/LICENSE.txt`
