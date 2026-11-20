using System;
using System.Threading;
using System.Windows.Threading;

namespace AngelinaISAssistant.Live;

/// <summary>
/// 前后端桥梁：把采集线程送来的帧交给后端，再把结果切回 UI 线程。
/// 同一时间只允许一个后端任务在跑；忙时保留最新一帧（而不是直接丢弃），
/// 保证「窗口最小化 / 画面切走」这种短暂变化后的最后一帧一定会被识别，
/// 不会把旧结果永远留在遮罩上。
/// </summary>
internal sealed class LiveAssistCoordinator : IDisposable
{
    private readonly ILiveAssistBackend _backend;
    private readonly Func<LiveAssistOptions> _options;
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _minInterval;
    private readonly CancellationTokenSource _cancellation = new();

    private readonly object _pendingGate = new();
    private CapturedFrame? _pendingFrame;
    private LiveAssistOptions _pendingOptions;
    private long _pendingGeneration;

    private long _lastProcessTicks;
    private int _busy;
    private int _cleared = 1;
    private bool _initialized;
    private bool _disposed;

    /// <summary>每次 Clear 递增；晚到的后台结果用它丢弃，保证停止采集后不会再更新悬浮层。</summary>
    private long _generation;

    /// <summary>没有任何勾选项时是否仍然运行后端（OCR 测试模式需要）。</summary>
    private readonly bool _processWithoutOptions;

    public LiveAssistCoordinator(
        ILiveAssistBackend backend,
        Func<LiveAssistOptions> options,
        Dispatcher dispatcher,
        TimeSpan? minInterval = null,
        bool processWithoutOptions = false)
    {
        _backend = backend;
        _options = options;
        _dispatcher = dispatcher;
        _minInterval = minInterval ?? TimeSpan.FromMilliseconds(250);
        _processWithoutOptions = processWithoutOptions;
    }

    /// <summary>结果在 UI 线程触发；参数为 null 表示清空。</summary>
    public event Action<LiveAssistResult?>? ResultsReady;

    public bool IsAvailable => _backend.IsAvailable;

    /// <summary>采集线程调用：非阻塞；忙时只保留最新帧，等当前识别结束后再跑。</summary>
    public void OfferFrame(byte[] pixels, int width, int height)
    {
        if (_disposed || pixels.Length == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        var options = _options();

        if (options == LiveAssistOptions.None && !_processWithoutOptions)
        {
            Clear();
            return;
        }

        var frame = new CapturedFrame(pixels, width, height);

        lock (_pendingGate)
        {
            _pendingFrame = frame;
            _pendingOptions = options;
            _pendingGeneration = Interlocked.Read(ref _generation);
        }

        Interlocked.Exchange(ref _cleared, 0);

        if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
        {
            _ = System.Threading.Tasks.Task.Run(() => ProcessPendingAsync(_cancellation.Token));
        }
    }

    private async System.Threading.Tasks.Task ProcessPendingAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                CapturedFrame? frame;
                LiveAssistOptions options;
                long generation;

                lock (_pendingGate)
                {
                    frame = _pendingFrame;
                    options = _pendingOptions;
                    generation = _pendingGeneration;
                    _pendingFrame = null;
                }

                if (frame is null)
                {
                    return;
                }

                var waitMs = _minInterval.TotalMilliseconds
                             - (Environment.TickCount64 - Interlocked.Read(ref _lastProcessTicks));

                if (waitMs > 0)
                {
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(waitMs), token)
                        .ConfigureAwait(false);
                }

                // 等待期间如果又来了更新的帧，优先识别最新的
                lock (_pendingGate)
                {
                    if (_pendingFrame is not null)
                    {
                        frame = _pendingFrame;
                        options = _pendingOptions;
                        generation = _pendingGeneration;
                        _pendingFrame = null;
                    }
                }

                Interlocked.Exchange(ref _lastProcessTicks, Environment.TickCount64);

                if (frame is null)
                {
                    continue;
                }

                var currentFrame = frame.Value;

                try
                {
                    if (!_initialized)
                    {
                        _backend.Initialize();
                        _initialized = true;
                    }

                    var result = _backend.Process(currentFrame, options);

                    // 期间已经停止采集 / 切换勾选：丢弃这次识别结果
                    if (Interlocked.Read(ref _generation) != generation)
                    {
                        continue;
                    }

                    Publish(result);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"live assist failed · {ex.Message}");

                    if (Interlocked.Read(ref _generation) == generation)
                    {
                        Publish(null);
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);

            // 处理「刚判空就来了新帧」的竞态：释放 busy 后再确认一次
            var hasPending = false;

            lock (_pendingGate)
            {
                hasPending = _pendingFrame is not null;
            }

            if (hasPending
                && !_disposed
                && !token.IsCancellationRequested
                && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
            {
                _ = System.Threading.Tasks.Task.Run(() => ProcessPendingAsync(token));
            }
        }
    }

    /// <summary>
    /// 推理设置变更后调用：丢弃当前后端（下次识别前重新初始化，读取新的 InferenceGpu 设置）。
    /// 正在运行的那一次识别结果会被 generation 校验丢弃。
    /// </summary>
    public void ResetBackend()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _cleared, 1);

        lock (_pendingGate)
        {
            _pendingFrame = null;
        }

        try
        {
            _backend.Dispose();
        }
        catch
        {
            // 旧后端释放失败不影响重新加载
        }

        _initialized = false;
        Publish(null);
    }

    /// <summary>清空悬浮内容（切换勾选、停止采集时调用）。</summary>
    public void Clear()
    {
        Interlocked.Increment(ref _generation);

        lock (_pendingGate)
        {
            _pendingFrame = null;
        }

        if (Interlocked.Exchange(ref _cleared, 1) == 1)
        {
            return;
        }

        Publish(null);
    }

    private void Publish(LiveAssistResult? result)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(() => ResultsReady?.Invoke(result));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();

        lock (_pendingGate)
        {
            _pendingFrame = null;
        }

        _cancellation.Dispose();
        _backend.Dispose();
    }
}
