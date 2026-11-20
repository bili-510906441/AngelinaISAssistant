using System;
using System.Collections.Generic;
using System.Windows;
using AngelinaISAssistant.Live;
using AngelinaISAssistant.Live.Ocr;

namespace AngelinaISAssistant;

/// <summary>
/// 实时辅助的前端逻辑：左侧勾选（局外 / 局内 / 通用）、截图帧交给后端、
/// 后端结果推送到悬浮窗。识别实现位于 Live\ 目录，前端不感知具体算法。
/// </summary>
public partial class MainWindow
{
    private LiveAssistCoordinator? _liveAssist;
    private PpOcrLiveAssistBackend? _ocrBackend;
    private LiveAssistResult? _liveOverlayResult;

    private void InitializeLiveAssist()
    {
        // 测试功能：默认开启 OCR 悬浮层；加 --no-ocr 启动参数（或环境变量 AISA_NO_OCR=1）
        // 可回退到旧的占位后端（不识别、不显示任何悬浮内容），NoopLiveAssistBackend 仍然保留。
        var noOcrEnv = Environment.GetEnvironmentVariable("AISA_NO_OCR");
        var ocrDisabled = Array.Exists(
                              Environment.GetCommandLineArgs(),
                              arg => string.Equals(arg, "--no-ocr", StringComparison.OrdinalIgnoreCase))
                          || string.Equals(noOcrEnv, "1", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(noOcrEnv, "true", StringComparison.OrdinalIgnoreCase);

        ILiveAssistBackend backend;

        if (ocrDisabled)
        {
            backend = new NoopLiveAssistBackend();
        }
        else
        {
            _ocrBackend = new PpOcrLiveAssistBackend();
            backend = _ocrBackend;
        }

        _liveAssist = new LiveAssistCoordinator(
            backend,
            ReadLiveOptions,
            Dispatcher,
            processWithoutOptions: !ocrDisabled);

        _liveAssist.ResultsReady += result =>
        {
            _liveOverlayResult = result;
            ApplyLiveOverlay();
        };

        if (LiveBackendStatus is not null)
        {
            LiveBackendStatus.Visibility = _liveAssist.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
        }

        Logger.Info(ocrDisabled
            ? "live assist · ocr test overlay disabled (--no-ocr), placeholder backend"
            : $"live assist · ocr test overlay enabled · backend available={_liveAssist.IsAvailable}");

        ApplyLiveOptions(AppSettings.Current.LiveAssist);

        // DirectML 首次推理有着色器编译开销（本机约 2–3 秒）：在窗口空闲后后台预热，
        // 避免用户开始采集后第一次识别等太久。
        Dispatcher.BeginInvoke(
            new Action(WarmupOcrBackendIfNeeded),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void WarmupOcrBackendIfNeeded()
    {
        var backend = _ocrBackend;

        if (backend is null
            || AppSettings.Current.InferenceGpu == -1
            || !OrtNative.DmlRuntimeFilesPresent)
        {
            return;
        }

        _ = System.Threading.Tasks.Task.Run(() => backend.Warmup());
    }

    private LiveAssistOptions ReadLiveOptions()
    {
        var options = LiveAssistOptions.None;

        if (LiveRelicRecord?.IsChecked == true) options |= LiveAssistOptions.RelicRecord;
        if (LiveStageValues?.IsChecked == true) options |= LiveAssistOptions.StageValues;
        if (LiveBattleAssist?.IsChecked == true) options |= LiveAssistOptions.BattleAssist;
        if (LiveEnemyHealth?.IsChecked == true) options |= LiveAssistOptions.EnemyHealth;
        if (LiveSpecialStage?.IsChecked == true) options |= LiveAssistOptions.SpecialStage;

        return options;
    }

    private void ApplyLiveOptions(LiveAssistOptions options)
    {
        if (LiveRelicRecord is null)
        {
            return;
        }

        LiveRelicRecord.IsChecked = options.HasFlag(LiveAssistOptions.RelicRecord);
        LiveStageValues.IsChecked = options.HasFlag(LiveAssistOptions.StageValues);
        LiveBattleAssist.IsChecked = options.HasFlag(LiveAssistOptions.BattleAssist);
        LiveEnemyHealth.IsChecked = options.HasFlag(LiveAssistOptions.EnemyHealth);
        LiveSpecialStage.IsChecked = options.HasFlag(LiveAssistOptions.SpecialStage);
    }

    private void LiveOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var options = ReadLiveOptions();

        AppSettings.Current.LiveAssist = options;
        AppSettings.Save();

        ClearLiveOverlay();
        Logger.Info($"live assist options · {options}");
    }

    private void ApplyLiveOverlay() => _maskOverlay?.UpdateOverlay(_liveOverlayResult);

    private void ClearLiveOverlay()
    {
        _liveOverlayResult = null;
        _ocrBackend?.ResetRunInfo();
        _liveAssist?.Clear();
        ApplyLiveOverlay();
    }
}
