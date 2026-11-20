# 实时辅助（PP-OCRv6_small OCR）

前端（左侧勾选、屏幕采集、悬浮窗）与后端通过 `ILiveAssistBackend` 解耦；
当前实现为 **PP-OCRv6_small det + rec** 的 ONNX Runtime 流水线（CPU / DirectML 两种执行提供器）。

## 数据流

```
ScreenCaptureService（WGC / DXGI / BitBlt）
        │  BGRA32 帧（物理像素）
        ▼
LiveAssistCoordinator   ← 限频 250ms、忙时保留最新帧稍后处理、复制帧数据后交给后台线程
        │  CapturedFrame{Pixels,Width,Height} + LiveAssistOptions
        ▼
PpOcrLiveAssistBackend.Process()
        │  det（DB 概率图）→ 连通域/最小外接矩形/unclip → 裁剪
        │  rec（CTC 贪心解码）→ OcrLine[]
        │  左下角难度徽章：裁剪 + 放大 → 再识别一次
        ▼
RunInfoInterpreter.Update()                     EnemyIntelReader.Update()
        │  关卡名（中/英/日）      →「识别到{关卡名}」   │  检测敌方情报页（8 个标签）
        │  主题/层数（tip233 规则）→「已进入{主题}第N层」 │  识别当前查看的敌人
        │  难度（左下角徽章）      →「识别到难度N」       │  计算数值 → 5 条文字标注
        ▼                                           ▼
LiveAssistResult{ Items（追加式提示）, Annotations（按帧重绘的数值标注） }
        ▼
MaskOverlayWindow.UpdateOverlay()
        ▼
左侧：第一行固定 AngelinaISAssistant v版本号，下面最多 6 行「[时间] [INF] ...」，已追加过的不重复；
敌方情报页数值：金色数字画在八个属性标签右侧（字母等级前面）
```

任何勾选项被勾上（`LiveAssistOptions != None`）都会跑同一套 OCR；
后续如果要做「收藏品记录 / 关卡数值」等语义识别，在 `Process` 里按 `options`
对 `OcrLine` 做二次过滤即可，前端不用改。

## 关卡名称识别

需求：**识别到关卡名时，遮罩追加一行 `[时间] [INF] 识别到{关卡名}`**。

- **数据来源**：tomimi.dev 的集成战略数据
  （`ro1.json … ro6.json`、`stage_name_lookup_table.json`、`stage_name_overwrite_table.json`、
  `ro_stage_data/roN/*.json`）。整个 tomimi-dev 仓库保留在
  `new_structure/user_resources/tomimi-dev-main`，后续做「关卡数值 / 路线模拟」等功能时继续取用。
- **支持中 / 英 / 日三种语言**的关卡名；标点是名字本身的一部分（`所守者，义` 能识别，
  逗号被 OCR 漏掉也按 `所守者义` 命中）；`谤天(单)/谤天(双)` 这类变体名归一化成 `谤天`；
  数据后缀（`_b`、`_dlc1`）也会去掉。
- **匹配方式**：归一化（去掉空白/标点、英文转小写、去掉 `ISW-NO` 等代码前缀）后与别名表
  **精确**比对，不做模糊匹配；置信度门槛 **0.6**。识别到后保留（粘性），换关卡追加新的一行，
  并记住 `levelId` 供敌方情报页确定关卡。
- **数据文件**：`Live/Intel/data/stage_names.json` 内嵌进 DLL，运行时不需要外部文件。

## 难度识别（0~18）

游戏在**屏幕左下角**的徽章里显示当前难度数字。后端在地图界面（底部出现 `零件箱`/`总估价`/`钱盒`
这类主题特征词）时，按**相对区域**（左上角 0%~10% 宽、88%~100% 高）裁剪并放大 3 倍单独识别，
取出 0~18 的数字；识别成功后每隔 8 帧复检一次（换局时难度会变），失败保留上一次结果。
难度数字会追加 `[时间] [INF] 识别到难度{N}`，并作为敌人数值计算的输入。

## 敌方情报页数值

需求：**敌方情报页里游戏只用 S/A/B/C/D 等级显示属性，太笼统，需要在属性标签右侧显示计算后的数值。**

- **触发条件**（全部满足才认为打开了敌方情报页）：识别到 `全部`、`精英`、`领袖`
  三个分类标签（OCR 连成一行也可以），以及 `生命值`、`攻击力`、`防御力`、`法术抗性`、
  `移动速度` 五个属性标签（页面判定条件）；`攻击速度`、`元素抗性`、`损伤抵抗` 识别到就一起显示；
  置信度门槛统一 **0.6**。
- **识别当前敌人**：在分类标签与属性标签之间的右侧区域找敌人名，与当前关卡（最近一次识别到的
  关卡名）的敌人列表做名字匹配（中/英/日）；切换敌人后数值自动更新。
- **计算链**（与 tomimi.dev 的 `parseStats` 对齐）：
  基础数值（敌人数据库 stats[level]）→ 关卡修正（`n_mods` / 紧急作战时 `elite_mods`、`all_mods`）
  先取整 → 叠加形态修正、难度修正（`difficulty <= 当前难度` 的所有效果）、楼层成长
  （`floorBuff.hp^层数`、`floorBuff.atk^层数`）、低难度补偿，最后按属性的取整/clamp 规则输出。
- **作战 / 紧急作战（与 tomimi 一致）**：地图右侧节点面板里、关卡名上方会显示「作战」或「紧急作战」，
  后端按它决定这条计算链：
  - 「作战」→ 关卡修正用 `n_mods`；
  - 「紧急作战」→ 关卡修正用 `elite_mods`（例如敌人攻击/防御/生命 ×1.2），并且部分敌人会按
    `elite_runes.enemy_replace` 替换成精英版（例如 `炮手` → `炮击组长`），数值按替换后的敌人计算；
  - 节点类型识别不到时保持上一次判断；点不同节点（普通/紧急）会自动切换。
- **前置条件**：必须同时识别到关卡名、难度、层数（层数决定楼层成长；缺一个就不显示数值，
  对应需求「没识别到层数就直接不用显示」）。
- **显示**：共八个属性 —— 生命值 / 攻击力 / 防御力 / 法术抗性 / 移动速度，以及补充的
  攻击速度 / 元素抗性 / 损伤抵抗；数字画在属性标签右侧：左列放在标签与游戏字母等级之间、右列统一放在字母等级右侧，
  位置由 OCR 文本框动态计算（不写死坐标），空间不够自动缩小字号；
  页面关闭后标注消失。
- **数字格式**：设置 → 性能设置 → 数值显示格式，可选 `35.8w`（万，一位小数）/
  科学计数法 `3.58e5` / 完整数字 `358000`。
- **暂不支持（已留接口）**：收藏品（遗物）、敌人临时数值（例如 ISW-DF 止变里「朔壳」随
  敛傲装置增多提高攻击力）、关卡类型条件效果、`Sp.` 形态覆盖、多形态敌人（目前只算形态 0）。
  接入方式见下面的「扩展接口」。

## 扩展接口

```csharp
// Live/Intel/EnemyStatCalculator.cs
internal interface IEnemyStatModifierProvider
{
    void Collect(EnemyStatContext context, EnemyStatModifierSink sink);
}
```

- 收藏品：实现该接口，把遗物的 effects 包成 `ModGroupData` 加到 `sink.Others`；
  如果某个收藏品会让楼层成长 +1（例如黑流树海「追忆」），设置 `sink.ExtraFloorCount`。
- 敌人临时数值：同样在 `Collect` 里根据敌人 key / 关卡 / 当前状态追加临时修正组。
- 其它功能（关卡数值表、敌人血量等）在 `PpOcrLiveAssistBackend.Process` 里按 `options`
  增加识别分支即可，`LiveAssistResult` 已经支持「追加式提示 + 按帧重绘标注」两类输出。
- 注册位置：`EnemyStatCalculator.Providers`（目前为空数组）。

## 数据与重新生成

内嵌数据（`Live/Intel/data/`，编译进 DLL）：

| 文件 | 内容 |
|---|---|
| `stage_names.json` | 1312 条关卡名别名（中/英/日、变体名归一化、levelId、主题） |
| `stages.json` | 463 个关卡的代码 / 主题 / 变体修正（n_mods、all_mods、elite_mods）/ 紧急作战敌人替换 / 敌人列表 |
| `enemies.json` | 关卡引用到的 626 个敌人：三语名字、类型、各等级数值、form_mods、traits |
| `difficulty.json` | 六个主题各难度的修正效果与楼层成长系数 |

重新生成（在 code 目录下）：

```bash
python version_tools/generate_intel_data.py
```

## 测试模式与回退开关（当前默认）

- **开始采集后**：即使一个勾选项都没勾，也会持续识别（`LiveAssistCoordinator(processWithoutOptions: true)`）。
- **显示内容**：第一行固定为 `AngelinaISAssistant v版本号`；下面最多 6 行
  `[HH:mm:ss] [INF] 识别到{关卡名}` / `[HH:mm:ss] [INF] 识别到难度{N}` /
  `[HH:mm:ss] [INF] 已进入{主题}第{数字}层（层名）`。同一条消息不会重复追加，旧行最多保留 6 行。
- **清空**：停止采集/重新开始时清空提示与数值标注，第一行标题始终保留。
- **不会识别到自己**：遮罩窗口设置了 `WDA_EXCLUDEFROMCAPTURE`，照常显示但不会出现在屏幕采集里。
- **停止采集后**：立即清空悬浮内容；`LiveAssistCoordinator` 用 generation 计数丢弃晚到的识别结果。
- **回退方案**：启动参数 `--no-ocr` 或环境变量 `AISA_NO_OCR=1` 会改用
  `NoopLiveAssistBackend`（不识别、不显示任何悬浮内容）。

## 目录与文件

| 路径 | 作用 |
|---|---|
| `Live/PpOcrLiveAssistBackend.cs` | `ILiveAssistBackend` 实现：模型加载、关卡/难度/层数提示、敌方情报页调度 |
| `Live/Ocr/PpOcrEngine.cs` | det+rec 流水线（缩放、裁剪、解码） |
| `Live/Ocr/OrtNative.cs` | ONNX Runtime C API 手写绑定（CPU 1.32 / DirectML 1.24） |
| `Live/Ocr/OcrRegion.cs` | 按相对区域裁剪 + 放大（左下角难度徽章识别用） |
| `Live/Ocr/RunInfoInterpreter.cs` | 关卡名 / 主题 / 层数 → 遮罩提示 |
| `Live/Intel/IsText.cs` | OCR 文本归一化（中英日关卡名 / 敌人名匹配用） |
| `Live/Intel/IsIntelData.cs` | 内嵌数据加载：关卡别名索引、敌人、难度、主题配置 |
| `Live/Intel/StatMath.cs` | tomimi 数值公式（compileMods / getModdedStat / parseStats） |
| `Live/Intel/EnemyStatCalculator.cs` | 敌人数值计算入口 + 收藏品/临时数值扩展接口 |
| `Live/Intel/EnemyIntelReader.cs` | 敌方情报页检测、敌人识别、数值标注生成 |
| `Live/Intel/StageNodeMode.cs` | 地图节点类型识别（作战 / 紧急作战） |
| `Live/Intel/DifficultyReader.cs` | 左下角难度数字解析 |
| `Live/Intel/NumberDisplay.cs` | 数值显示格式（万 / 科学计数法 / 完整数字） |
| `Live/Intel/data/` | 内嵌数据（脚本生成，勿手改） |
| `version_tools/generate_intel_data.py` | 从 tomimi.dev 重新生成内嵌数据 |
| `models/ppocr/` | `PP-OCRv6_small_det.onnx`、`PP-OCRv6_small_rec.onnx`、`keys.txt` |

## GPU（DirectML）推理

`OrtNative` 优先加载 `externals\onnxruntime_dml\onnxruntime.dll`（DirectML 版），
按「设置 → 性能设置 → 使用 GPU 加速推理」决定是否追加 DirectML Execution Provider：

- `-1` 使用CPU推理 → 强制 CPU
- `-2` 系统默认 → DirectML 可用则用 device 0，否则 CPU
- `>=0` 具体显卡 → 显示适配器序号作为 DirectML `device_id`

DirectML 初始化失败会自动回退 CPU；设置改变时会通过 `LiveAssistCoordinator.ResetBackend()`
重新加载。GPU 运行时只有约 36 MB，不需要 CUDA/cuDNN。详见 `Ocr/GPU_DIRECTML_GUIDE.md`。

## 参数与过滤

det 后处理：`thresh=0.2`、`box_thresh=0.45`、`unclip_ratio=1.4`、`max_candidates=3000`；
det 输入最长边缩到 1600；rec 高固定 48。

- 所有语义识别（关卡名 / 主题 / 层数 / 难度 / 敌方情报页标签）置信度门槛统一 **0.6**
- 遮罩只收到语义提示与数值标注，不输出原始 OCR 文字、置信度或识别框
- 单帧原始 OCR 最多 80 行；竖长文本按 PaddleOCR 规则逆时针旋转 90° 后再识别

## 测试

```
dotnet run --project auto_test/OcrTest/OcrTest.csproj -c Release
# DirectML：
dotnet run --project auto_test/OcrTest/OcrTest.csproj -c Release -- --dml
# 诊断单张截图（打印每行 OCR 结果、关卡名匹配、难度探针；可选模拟敌方情报页计算）：
dotnet run --project auto_test/OcrTest/OcrTest.csproj -c Release -- --image <图片> [--stage 关卡名 --difficulty 9 --layer 1]
```

测试覆盖：det→rec 基础识别、关卡名（中英日 / 标点 / 变体）、难度数字、真实截图 OCR 结果的
敌方情报页八项数值（2756/318/200/25/1.9/2/0/0 与 8268/975/1000/40/0.6/2/0/0）、作战/紧急作战数值差异与敌人替换、数字格式、遮罩多行追加/去重/清空、
以及「一帧同时出现地图与敌方情报特征」的完整后端链路；SmokeTest 还覆盖关闭主窗口时遮罩窗口必须真正关闭（否则进程会残留）。

## 注意事项

- `Process` 在后台线程执行，禁止直接访问任何 UI 控件；帧数据由协调器复制后传入。
- 一次整屏识别通常 0.5–1.5 秒（1600 长边）；地图界面会多一次左下角小图识别（约 0.1–0.2 秒）。
- ONNX Runtime 的 C API 通过版本表访问（不 DllImport 具体函数），不会和 App 的 `SetDllImportResolver` 冲突。
- 数值计算只覆盖「沉沦者的黑流树海」和「岁的界园志异」两个主题（其余主题目前只识别关卡名/难度，
  楼层成长公式与低难度补偿的差异尚未接入）；敌人等级取关卡数据里的 level，多形态只算形态 0。
