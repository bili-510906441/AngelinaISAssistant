using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AngelinaISAssistant.Live.Intel;
using AngelinaISAssistant.Live.Ocr;

namespace AngelinaISAssistant.Live;

/// <summary>
/// 实时辅助的正式后端：PP-OCRv6_small（det + rec）检测并识别屏幕文字。
/// 模型位于 &lt;程序目录&gt;\models\ppocr，随发布包一起分发。
/// </summary>
internal sealed class PpOcrLiveAssistBackend : ILiveAssistBackend
{
    /// <summary>左下角难度徽章的裁剪区域与放大倍数（相对比例，分辨率 / UI 缩放变化时仍然适用）。</summary>
    private const double DifficultyRegionLeft = 0.0;
    private const double DifficultyRegionTop = 0.88;
    private const double DifficultyRegionRight = 0.10;
    private const double DifficultyRegionBottom = 1.0;
    private const double DifficultyRegionScale = 3.0;

    /// <summary>难度只在「地图界面」刷新；识别成功后每隔几帧复检一次（换局时难度会变）。</summary>
    private const int DifficultyProbeInterval = 8;

    private readonly object _gate = new();
    private readonly RunInfoInterpreter _runInfo = new();
    private readonly EnemyIntelReader _enemyIntel = new();
    private PpOcrEngine? _engine;
    private string _loadError = string.Empty;
    private int _warmupStarted;
    private int? _difficulty;
    private int _difficultyProbeCounter;
    private bool _eliteMode;

    public static string ModelDirectory => Path.Combine(AppContext.BaseDirectory, "models", "ppocr");

    public bool IsAvailable => PpOcrEngine.ModelFilesPresent(ModelDirectory) && OrtNative.NativeLibraryPresent;

    public string LoadError => _loadError;

    public OrtExecutionProvider ActiveProvider => _engine?.Provider ?? OrtExecutionProvider.Cpu;

    public int ActiveDeviceId => _engine?.DeviceId ?? -1;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_engine is not null)
            {
                return;
            }

            try
            {
                var requested = ResolveRequestedProvider(out var deviceId);
                var watch = Stopwatch.StartNew();
                _engine = PpOcrEngine.Load(ModelDirectory, requested, deviceId);
                watch.Stop();

                var providerName = _engine.IsDml ? "dml" : "cpu";
                var device = _engine.IsGpu ? $" device={_engine.DeviceId}" : string.Empty;
                Logger.Info(
                    $"ocr backend ready · provider={providerName}{device} · runtime={OrtNative.RuntimeKind} · " +
                    $"onnxruntime {OrtNative.Version} · loaded in {watch.ElapsedMilliseconds} ms");

                // 预载内嵌的集成战略数据（关卡名 / 敌人 / 难度），首次识别时不用再等解析
                if (IsIntelData.IsAvailable)
                {
                    Logger.Info(
                        $"intel data ready · {IsIntelData.Data.Stages.Count} stages · " +
                        $"{IsIntelData.Data.Enemies.Count} enemies · {IsIntelData.Data.StageNames.Count} aliases");
                }
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                Logger.Warn($"ocr backend init failed · {ex.Message}");
            }
        }
    }

    /// <summary>后台预热（DirectML）：首次推理的 DML 着色器编译有开销，提前跑一次空白输入。</summary>
    public void Warmup()
    {
        if (Interlocked.Exchange(ref _warmupStarted, 1) == 1)
        {
            return;
        }

        try
        {
            Initialize();

            var engine = _engine;

            if (engine is null || !engine.IsGpu)
            {
                return;
            }

            engine.Warmup();
        }
        catch (ObjectDisposedException)
        {
            // 预热期间设置被切换/程序关闭
        }
        catch (Exception ex)
        {
            Logger.Warn($"ocr gpu warmup failed · {ex.Message}");
        }
    }

    /// <summary>设置变更后丢弃已加载引擎，下一次识别时按新设置重新加载。</summary>
    public void Reset()
    {
        PpOcrEngine? old;

        lock (_gate)
        {
            old = _engine;
            _engine = null;
            _loadError = string.Empty;
            Interlocked.Exchange(ref _warmupStarted, 0);
            ResetState();
        }

        // 旧引擎可能还在后台预热/识别；异步释放，避免卡住 UI 线程
        DisposeEngineAsync(old);
    }

    private void ResetState()
    {
        _runInfo.Reset();
        _enemyIntel.Reset();
        _difficulty = null;
        _difficultyProbeCounter = 0;
        _eliteMode = false;
    }

    private static void DisposeEngineAsync(PpOcrEngine? engine)
    {
        if (engine is null)
        {
            return;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                engine.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warn($"ocr engine dispose failed · {ex.Message}");
            }
        });
    }

    private static OrtExecutionProvider ResolveRequestedProvider(out int deviceId)
    {
        deviceId = -1;

        var disabled = Array.Exists(
                           Environment.GetCommandLineArgs(),
                           arg => string.Equals(arg, "--no-gpu", StringComparison.OrdinalIgnoreCase))
                       || string.Equals(Environment.GetEnvironmentVariable("AISA_NO_GPU"), "1", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(Environment.GetEnvironmentVariable("AISA_NO_GPU"), "true", StringComparison.OrdinalIgnoreCase);

        if (disabled)
        {
            return OrtExecutionProvider.Cpu;
        }

        var setting = AppSettings.Current.InferenceGpu;

        if (setting == -1)
        {
            return OrtExecutionProvider.Cpu;
        }

        // 触发 onnxruntime/dml 运行时加载，再判断 DmlAvailable
        OrtNative.EnsureInitialized();

        if (OrtNative.DmlAvailable)
        {
            deviceId = ResolveDmlDeviceId(setting, GpuCatalog.Devices);
            return OrtExecutionProvider.Dml;
        }

        Logger.Info("ocr dml runtime not found · using cpu");
        return OrtExecutionProvider.Cpu;
    }

    private static int ResolveDmlDeviceId(int setting, IReadOnlyList<string> displayDevices)
    {
        // DirectML 的 device_id 是 DXGI 适配器序号；这里按显示适配器列表顺序近似映射，
        // 只有一块 GPU（绝大多数情况）时直接 0。
        if (setting >= 0 && setting < displayDevices.Count)
        {
            return setting;
        }

        return 0;
    }

    public LiveAssistResult? Process(CapturedFrame frame, LiveAssistOptions options)
    {
        var engine = _engine;

        if (engine is null)
        {
            return null;
        }

        try
        {
            var lines = engine.Recognize(frame.Pixels, frame.Width, frame.Height);
            var runInfo = _runInfo.Update(lines, frame.Width, frame.Height);

            // 地图界面（能看到主题特征词）时，顺便读一下左下角难度徽章
            if (HasThemeHint(lines))
            {
                TryRefreshDifficulty(engine, frame);
            }

            _eliteMode = ResolveEliteMode(lines, frame.Width, frame.Height, runInfo);

            // 遮罩左侧的追加式提示：识别到关卡名 / 难度 / 已进入第几层
            var messages = BuildMessages(runInfo);

            // 敌方情报页：识别当前查看的敌人，计算数值并生成标注
            var annotations = _enemyIntel.Update(
                lines,
                frame.Width,
                frame.Height,
                _runInfo.GetStageMatches(),
                _difficulty,
                runInfo?.Layer,
                _eliteMode);

            if (messages.Count == 0 && annotations.Count == 0)
            {
                return new LiveAssistResult(Array.Empty<OverlayItem>());
            }

            var items = new OverlayItem[messages.Count];

            for (var i = 0; i < messages.Count; i++)
            {
                items[i] = new OverlayItem(OverlayKind.Info, messages[i], 0, 0, 0, 0);
            }

            return new LiveAssistResult(items, annotations);
        }
        catch (ObjectDisposedException)
        {
            // 关闭程序时后台识别还没跑完，属于正常退出流程
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"ocr recognize failed · {ex.Message}");
            return null;
        }
    }

    private List<string> BuildMessages(RunInfo? runInfo)
    {
        var messages = new List<string>(3);

        if (runInfo?.Stage is not null)
        {
            messages.Add($"识别到{runInfo.Stage.Display}");
        }

        if (_difficulty is int difficulty)
        {
            messages.Add($"识别到难度{difficulty}");
        }

        var runMessage = runInfo is null ? null : RunInfoInterpreter.BuildOverlayMessage(runInfo);

        if (!string.IsNullOrEmpty(runMessage))
        {
            messages.Add(runMessage);
        }

        return messages;
    }

    /// <summary>当前帧是否是「地图界面」（底部有主题特征词：零件箱 / 总估价 / 钱盒）。</summary>
    private static bool HasThemeHint(IReadOnlyList<OcrLine> lines)
    {
        foreach (var line in lines)
        {
            if (line.Confidence < RunInfoInterpreter.MinConfidence)
            {
                continue;
            }

            if (line.Text.Contains("零件箱", StringComparison.Ordinal)
                || line.Text.Contains("总估价", StringComparison.Ordinal)
                || line.Text.Contains("钱盒", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>裁剪左下角区域单独识别难度数字；识别失败时保留上一次结果。</summary>
    private void TryRefreshDifficulty(PpOcrEngine engine, CapturedFrame frame)
    {
        if (_difficulty is not null && _difficultyProbeCounter++ % DifficultyProbeInterval != 0)
        {
            return;
        }

        var (pixels, width, height) = OcrRegion.CropScaled(
            frame.Pixels,
            frame.Width,
            frame.Height,
            DifficultyRegionLeft,
            DifficultyRegionTop,
            DifficultyRegionRight,
            DifficultyRegionBottom,
            DifficultyRegionScale);

        var lines = engine.Recognize(pixels, width, height);

        if (DifficultyReader.TryParse(lines, out var difficulty))
        {
            if (_difficulty != difficulty)
            {
                Logger.Info($"difficulty recognized · {difficulty}");
            }

            _difficulty = difficulty;
        }
    }

    /// <summary>
    /// 判断当前节点是不是「紧急作战」：地图右侧面板里、关卡名上方会显示「作战」/「紧急作战」。
    /// 识别不到时保持上一次判断。
    /// </summary>
    private bool ResolveEliteMode(IReadOnlyList<OcrLine> lines, int frameWidth, int frameHeight, RunInfo? runInfo)
    {
        if (runInfo?.Stage is null)
        {
            return _eliteMode;
        }

        var detected = StageNodeMode.Detect(lines, frameWidth, frameHeight, runInfo.Stage.Display);

        if (detected is bool elite)
        {
            _eliteMode = elite;
        }

        return _eliteMode;
    }
    /// <summary>停止采集/重新开始时清掉上次识别到的主题、层数、难度与关卡。</summary>
    public void ResetRunInfo()
    {
        lock (_gate)
        {
            ResetState();
        }
    }

    public void Dispose()
    {
        PpOcrEngine? old;

        lock (_gate)
        {
            old = _engine;
            _engine = null;
            ResetState();
        }

        DisposeEngineAsync(old);
    }
}
