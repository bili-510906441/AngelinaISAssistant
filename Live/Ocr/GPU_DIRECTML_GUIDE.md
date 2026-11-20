# DirectML GPU 推理接入说明

> 状态：**已接入并通过本机验证**（RTX 5060 Laptop，Windows 11）。
> 对齐设置：`设置 → 性能设置 → 使用 GPU 加速推理`（`AppSettings.InferenceGpu`）。
> 空间：GPU 运行时约 **36 MB**（ONNX Runtime DirectML 版 + DirectML.dll），
> 不再需要 CUDA Toolkit / cuDNN（约 1.2 GB）。

---

## 0. 为什么用 DirectML

- 和 MAA 同类方案：`onnxruntime.dll` + `DirectML.dll` 两个文件即可用 N 卡 / A 卡 / I 卡加速，
  不安装 CUDA、不携带 cuDNN，发布包只增加约 36 MB。
- DirectML 是 Windows 自带的 D3D12 推理后端，通过 `OrtSessionOptionsAppendExecutionProvider_DML`
  导出函数挂到 ONNX Runtime 上。
- 代价：比 CUDA 慢一些（本机实测稳态约 240–470 ms/帧，CUDA 约 90–170 ms，
  CPU 约 700–800 ms），但体积只有 CUDA 方案的约 1/30。
- 首次推理需要编译 DML 着色器，程序启动后会在后台预热（约 2–3 秒）。

---

## 1. 本机环境与实测

| 项 | 值 |
|---|---|
| GPU | NVIDIA GeForce RTX 5060 Laptop（8 GB，Blackwell） |
| 驱动 | 617.42 |
| ONNX Runtime（DirectML） | **1.24.4**（`OrtApi` v24） |
| DirectML | **1.15.4** |
| 测试图 | 1600×900，4 行文字 |

实测（`OcrTest --dml`）：

```
engine loaded in 296 ms · provider=Dml · dmlAvailable=True · ort=1.24.4 · device=0
gpu warmup: 2857 ms
recognize: 4 line(s) in 376 ms      ← 预热后第一帧
gpu recognize pass 2: 4 line(s) in 238 ms
gpu recognize pass 3: 4 line(s) in 237 ms
OCR TEST OK
```

CPU 对比约 700–800 ms/帧。

---

## 2. 文件与来源

### 2.1 目录

```
code\
  libs\
    onnxruntime\              # CPU 版（1.32.0，保留）
      onnxruntime.dll
    onnxruntime_dml\          # DirectML 版（随发布包分发）
      onnxruntime.dll                           17,328,152  (ORT 1.24.4)
      onnxruntime_providers_shared.dll              22,040
      DirectML.dll                              18,527,776  (DirectML 1.15.4)
      ORT-LICENSE.txt
      DirectML-LICENSE.txt
      DirectML-ThirdPartyNotices.txt
  models\ppocr\
```

发布时 `csproj` 复制到 `release\externals\onnxruntime_dml\`。

### 2.2 下载来源

1. **ONNX Runtime 1.24.4 DirectML（Windows x64）**

   ```
   https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/1.24.4/microsoft.ml.onnxruntime.directml.1.24.4.nupkg
   ```

   解压后取 `runtimes\win-x64\native\` 下：

   - `onnxruntime.dll`
   - `onnxruntime_providers_shared.dll`

2. **Microsoft.AI.DirectML 1.15.4**

   ```
   https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.4/microsoft.ai.directml.1.15.4.nupkg
   ```

   只需要 `bin\x64-win\DirectML.dll`（整包约 200 MB，其余是其它架构/Xbox 版本）。

### 2.3 版本注意

- ORT DirectML 最新发布到 **1.24.4**（之后没有新的 DirectML NuGet 包），
  它的 `OrtApi` 是 **v24**；CPU 版是 1.32（v32）。
- `OrtNative` 会按构建探测 API 版本：DirectML 构建先请求 v24，CPU 构建先请求 v32，
  找不到再向下探测。
- DirectML 版 ORT 也包含 CPU EP；DML 初始化失败时可以回退 CPU session。
- 不需要 CUDA / cuDNN / `cuda_nvrtc` 等任何 NVIDIA 运行时。

---

## 3. 代码接入

| 文件 | 作用 |
|---|---|
| `Live/Ocr/OrtNative.cs` | 优先加载 `externals\onnxruntime_dml\onnxruntime.dll`；用导出函数 `OrtSessionOptionsAppendExecutionProvider_DML(options, device_id)` 追加 DML EP；失败回退 CPU |
| `Live/Ocr/OrtModel.cs` | session 接受 `OrtExecutionProvider` + `deviceId` |
| `Live/Ocr/PpOcrEngine.cs` | `Load(dir, requested, deviceId)`；DML 失败自动回退 CPU；`Warmup()` 预热 DML 着色器 |
| `Live/PpOcrLiveAssistBackend.cs` | 读取 `InferenceGpu`：`-1` CPU；`-2`/`>=0` 使用 DirectML（`device_id` 映射见 §4）；`Reset()` 支持设置变更 |
| `Live/LiveAssistCoordinator.cs` | 忙时保留最新帧（避免窗口最小化后最后一帧被丢弃导致遮罩残留）；设置变更 `ResetBackend()` |
| `MainWindow.xaml.cs` / `MainWindow.LiveAssist.cs` | 设置变更重载后端；窗口空闲后台预热 DML |
| `AngelinaISAssistant.csproj` | 把 `libs\onnxruntime_dml\**` 复制到输出和 `release\externals\onnxruntime_dml\` |
| `auto_test/OcrTest` | `--dml`（`--gpu` 作为兼容别名）强制 DirectML 路径 |
| `.gitignore` | 忽略 `libs/onnxruntime_dml/*.dll`（二进制不入库） |

### 3.1 DLL 加载顺序

`OrtNative`：

1. `externals\onnxruntime_dml\onnxruntime.dll`（发布包）
2. `onnxruntime_dml\onnxruntime.dll`
3. `libs\onnxruntime_dml\onnxruntime.dll`（开发目录）

加载前把该目录加入 DLL 搜索路径（`SetDefaultDllDirectories` + `AddDllDirectory`），
保证 `onnxruntime.dll` 能找到同目录的 `DirectML.dll`。

环境变量覆盖（排查用）：

| 变量 | 作用 |
|---|---|
| `AISA_ORT_DML_DIR` | 指向 `onnxruntime.dll` 所在目录 |
| `AISA_NO_GPU=1` | 强制 CPU（等同 `--no-gpu`） |

---

## 4. 与「性能设置 → 使用 GPU 加速推理」对齐

| `InferenceGpu` | 行为 |
|---|---|
| `-1` 使用CPU推理 | 强制 CPU，不创建 DML session |
| `-2` 系统默认 | DirectML 可用时用 DML（device_id=0），否则 CPU |
| `>=0` 具体显卡 | 使用 `GpuCatalog.Devices[index]` 对应的 DXGI 适配器序号作为 DML `device_id`；越界/失败回退 CPU |

说明：DirectML 的 `device_id` 是 DXGI 适配器序号。当前实现按显示适配器列表顺序近似映射；
绝大多数单显卡/双显卡（核显 + 独显）机器上，`-2` 也会落到可用的 GPU 上。
如果以后需要精确选择，可以改为枚举 DXGI 适配器名称做匹配。

---

## 5. 回退链

1. DirectML 运行时不完整/加载失败 → CPU 版 `libs\onnxruntime`；
2. DML EP 追加或 session 创建失败 → 同一个 DirectML ORT 创建 CPU session；
3. 运行中识别异常 → 返回 null，不影响 UI；
4. 设置 `-1`、`--no-gpu`、`AISA_NO_GPU=1` → 永远 CPU；
5. 删除 `release\externals\onnxruntime_dml\` → 启动后自动 CPU（功能完整，只是慢）。

---

## 6. 测试

```
# CPU
dotnet run --project auto_test/OcrTest/OcrTest.csproj -c Release

# DirectML
dotnet run --project auto_test/OcrTest/OcrTest.csproj -c Release -- --dml
```

期望：

- `provider=Dml`、`ort=1.24.4`、`device=0`
- `gpu warmup` 约 2–3 秒（仅启动预热一次）
- 稳态约 240–470 ms/帧，识别文本与 CPU 一致
- `OCR TEST OK`

应用层：开始采集后日志出现

```
onnxruntime ready · 1.24.4 · runtime=dml · api=v24 · providers=DmlExecutionProvider
ocr backend ready · provider=dml device=0 · runtime=dml · onnxruntime 1.24.4
ocr dml warmup done in ...
```

---

## 7. 已知问题 / 注意

1. **DirectML 版本固定 1.24.4**（ORT API v24）；不要直接换成 1.32 的 GPU 包
   （那是 CUDA 版，API/依赖完全不同）。
2. 首次 DML 着色器编译由后台预热承担；如果启动后立即开始采集，第一帧仍可能等 1–3 秒。
3. DirectML 对部分算子不支持时会自动落到 CPU EP，速度会下降，但结果仍正确。
4. `device_id` 是 DXGI 序号；多显卡且显示适配器顺序与 DXGI 不一致时，选择具体显卡可能不精确。
5. 体积：`externals\onnxruntime_dml\` 约 36 MB；CPU 版约 21 MB；v0.1.2 完整发布包实测 **513 个文件 / 283.6 MB**。

---

## 8. 回退/移除

- 设置「使用CPU推理」即可，不动文件；
- 或删除 `release\externals\onnxruntime_dml\`；
- 代码回退：`OrtNative` 未找到 DML 构建时自动加载 CPU 版，旧 CPU 路径未改动。
