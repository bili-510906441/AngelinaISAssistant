using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AngelinaISAssistant.Live.Ocr;

/// <summary>识别出来的一行文字（坐标是原截图的物理像素，已转成轴对齐框）。</summary>
internal sealed record OcrLine(string Text, float Confidence, float X, float Y, float Width, float Height);

/// <summary>
/// PP-OCRv6_small det + rec 的完整流水线：缩放 → 检测 → 裁剪 → 识别 → CTC 解码。
/// </summary>
internal sealed class PpOcrEngine : IDisposable
{
    public const string DetFileName = "PP-OCRv6_small_det.onnx";
    public const string RecFileName = "PP-OCRv6_small_rec.onnx";
    public const string KeysFileName = "keys.txt";

    /// <summary>
    /// 检测时最长边的上限。模型默认是 736/min（截图通常不满足 min&lt;736 而完全不缩放），
    /// 对 2560×1600 的游戏画面太慢，这里改为等比缩到最长边 1600。
    /// </summary>
    public const int DetLimitSideLen = 1600;

    public const int MaxLines = 80;
    public const float MinConfidence = 0.25f;

    private const int DetThreads = 4;
    private const int RecThreads = 2;

    private readonly OrtModel _det;
    private readonly OrtModel _rec;
    private readonly OcrKeys _keys;
    private readonly int _expectedClasses;
    private readonly object _gate = new();

    private bool _warnedClasses;

    private PpOcrEngine(string directory, OrtExecutionProvider provider, int deviceId)
    {
        Provider = provider;
        DeviceId = deviceId;
        _keys = OcrKeys.Load(Path.Combine(directory, KeysFileName));
        _expectedClasses = _keys.Count + 1;
        _det = new OrtModel(Path.Combine(directory, DetFileName), DetThreads, provider, deviceId);
        _rec = new OrtModel(Path.Combine(directory, RecFileName), RecThreads, provider, deviceId);
    }

    /// <summary>实际使用的推理后端。</summary>
    public OrtExecutionProvider Provider { get; }

    /// <summary>DirectML 设备号（CPU 模式下无意义）。</summary>
    public int DeviceId { get; }

    public bool IsDml => Provider == OrtExecutionProvider.Dml;

    public bool IsGpu => Provider != OrtExecutionProvider.Cpu;

    public static bool ModelFilesPresent(string directory) =>
        File.Exists(Path.Combine(directory, DetFileName))
        && File.Exists(Path.Combine(directory, RecFileName))
        && File.Exists(Path.Combine(directory, KeysFileName));

    public static PpOcrEngine Load(string directory) => Load(directory, OrtExecutionProvider.Cpu, 0);

    /// <summary>
    /// 加载引擎；requested==Dml 时如果 DirectML 不可用/会话创建失败，会自动回退 CPU 并记录日志。
    /// </summary>
    public static PpOcrEngine Load(string directory, OrtExecutionProvider requested, int deviceId)
    {
        if (requested == OrtExecutionProvider.Cpu)
        {
            return new PpOcrEngine(directory, OrtExecutionProvider.Cpu, 0);
        }

        try
        {
            return new PpOcrEngine(directory, requested, deviceId);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ocr {requested.ToString().ToLowerInvariant()} init failed, fallback to cpu · {ex.Message}");
            return new PpOcrEngine(directory, OrtExecutionProvider.Cpu, 0);
        }
    }

    public string KeysPath => Path.Combine(AppContext.BaseDirectory, "models", "ppocr", KeysFileName);

    public IReadOnlyList<OcrLine> Recognize(byte[] bgra, int width, int height)
    {
        if (bgra.Length == 0 || width <= 0 || height <= 0)
        {
            return Array.Empty<OcrLine>();
        }

        lock (_gate)
        {
            var timing = string.Equals(Environment.GetEnvironmentVariable("AISA_OCR_TIMING"), "1", StringComparison.Ordinal);
            var totalWatch = timing ? Stopwatch.StartNew() : null;
            var maxSide = Math.Max(width, height);
            var ratio = maxSide > DetLimitSideLen ? (double)DetLimitSideLen / maxSide : 1.0;
            var resizeWidth = Math.Max(32, (int)Math.Round(width * ratio / 32.0) * 32);
            var resizeHeight = Math.Max(32, (int)Math.Round(height * ratio / 32.0) * 32);

            var bgr = OcrImage.BgraToBgr(bgra, width, height);
            var resized = OcrImage.ResizeBilinear(bgr, width, height, resizeWidth, resizeHeight);

            var detInput = new float[checked(resizeWidth * resizeHeight * 3)];
            OcrImage.FillDetInput(resized, resizeWidth, resizeHeight, detInput);

            var detWatch = timing ? Stopwatch.StartNew() : null;
            var probability = _det.Run(
                detInput,
                new long[] { 1, 3, resizeHeight, resizeWidth },
                out var detShape);
            detWatch?.Stop();

            if (detShape.Length != 4 || detShape[0] != 1 || detShape[1] != 1)
            {
                throw new InvalidOperationException($"unexpected det output shape: [{string.Join(",", detShape)}]");
            }

            var probHeight = checked((int)detShape[2]);
            var probWidth = checked((int)detShape[3]);

            if (probability.Length < probWidth * probHeight)
            {
                throw new InvalidOperationException("det output tensor is smaller than its shape");
            }

            var boxes = DbPostProcess.Run(probability, probWidth, probHeight);

            if (boxes.Count == 0)
            {
                return Array.Empty<OcrLine>();
            }

            var scaleX = (float)width / probWidth;
            var scaleY = (float)height / probHeight;

            if (timing)
            {
                Console.WriteLine($"[ocr timing] det {detWatch!.ElapsedMilliseconds} ms · boxes={boxes.Count}");
            }

            var lines = new List<OcrLine>(Math.Min(boxes.Count, MaxLines));
            var recWatch = timing ? Stopwatch.StartNew() : null;

            foreach (var box in boxes)
            {
                var topLeft = Clamp(box.TopLeft, scaleX, scaleY, width, height);
                var topRight = Clamp(box.TopRight, scaleX, scaleY, width, height);
                var bottomRight = Clamp(box.BottomRight, scaleX, scaleY, width, height);
                var bottomLeft = Clamp(box.BottomLeft, scaleX, scaleY, width, height);

                var crop = OcrImage.CropRotatedRect(
                    bgr, width, height,
                    topLeft, topRight, bottomRight, bottomLeft,
                    out var cropWidth, out var cropHeight);

                if (cropWidth < 4 || cropHeight < 4)
                {
                    continue;
                }

                var recInput = OcrImage.BuildRecInput(crop, cropWidth, cropHeight, out var recWidth);
                var probabilities = _rec.Run(
                    recInput,
                    new long[] { 1, 3, OcrImage.RecHeight, recWidth },
                    out var recShape);

                if (recShape.Length != 3)
                {
                    continue;
                }

                var timeSteps = checked((int)recShape[1]);
                var classes = checked((int)recShape[2]);

                if (classes != _expectedClasses)
                {
                    if (!_warnedClasses)
                    {
                        _warnedClasses = true;
                        Logger.Warn($"ocr rec classes mismatch · model={classes} keys={_keys.Count}");
                    }

                    continue;
                }

                var text = CtcDecoder.Decode(probabilities, timeSteps, classes, _keys.Items, out var confidence).Trim();

                if (text.Length == 0 || confidence < MinConfidence)
                {
                    continue;
                }

                var minX = Math.Min(Math.Min(topLeft.X, topRight.X), Math.Min(bottomRight.X, bottomLeft.X));
                var maxX = Math.Max(Math.Max(topLeft.X, topRight.X), Math.Max(bottomRight.X, bottomLeft.X));
                var minY = Math.Min(Math.Min(topLeft.Y, topRight.Y), Math.Min(bottomRight.Y, bottomLeft.Y));
                var maxY = Math.Max(Math.Max(topLeft.Y, topRight.Y), Math.Max(bottomRight.Y, bottomLeft.Y));

                lines.Add(new OcrLine(text, confidence, minX, minY, maxX - minX, maxY - minY));

                if (lines.Count >= MaxLines)
                {
                    break;
                }
            }

            if (timing)
            {
                Console.WriteLine($"[ocr timing] rec {recWatch!.ElapsedMilliseconds} ms · lines={lines.Count} · total {totalWatch!.ElapsedMilliseconds} ms");
            }

            return Deduplicate(lines);
        }
    }

    /// <summary>
    /// 同一段文字如果被 det 重复框出（框高度重叠），只保留置信度最高的一条，
    /// 避免悬浮层在多次识别之间叠加重复文字。
    /// </summary>
    private static List<OcrLine> Deduplicate(List<OcrLine> lines)
    {
        if (lines.Count <= 1)
        {
            return lines;
        }

        var candidates = new List<OcrLine>(lines);
        candidates.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

        var kept = new List<OcrLine>(candidates.Count);

        foreach (var line in candidates)
        {
            var duplicate = false;

            foreach (var other in kept)
            {
                if (!string.Equals(other.Text.Trim(), line.Text.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                if (IntersectionOverUnion(other, line) >= 0.5f)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                kept.Add(line);
            }
        }

        kept.Sort((a, b) =>
        {
            var compare = a.Y.CompareTo(b.Y);
            return compare != 0 ? compare : a.X.CompareTo(b.X);
        });

        return kept;
    }

    private static float IntersectionOverUnion(OcrLine a, OcrLine b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);

        if (right <= left || bottom <= top)
        {
            return 0;
        }

        var intersection = (right - left) * (bottom - top);
        var union = a.Width * a.Height + b.Width * b.Height - intersection;

        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>
    /// 预热 DirectML 内核：首次推理会触发 DML 着色器编译，
    /// 在后台线程用接近真实尺寸的空白输入先跑一次，把这笔开销移到用户开始采集之前。
    /// </summary>
    public void Warmup()
    {
        lock (_gate)
        {
            if (!IsGpu)
            {
                return;
            }

            Logger.Info($"ocr {Provider.ToString().ToLowerInvariant()} warmup started");

            var watch = Stopwatch.StartNew();

            try
            {
                // 960p 级别屏幕对应的 det 输入尺寸（长边 1600，32 的倍数）
                var detInput = new float[checked(3 * 992 * 1600)];
                _det.Run(detInput, new long[] { 1, 3, 992, 1600 }, out _);

                // 典型文字行宽度，顺便把 rec 的卷积内核也编译了
                var recInput = new float[checked(3 * OcrHeightForWarmup * 320)];
                _rec.Run(recInput, new long[] { 1, 3, OcrHeightForWarmup, 320 }, out _);
            }
            finally
            {
                watch.Stop();
                Logger.Info($"ocr {Provider.ToString().ToLowerInvariant()} warmup done in {watch.ElapsedMilliseconds} ms");
            }
        }
    }

    private const int OcrHeightForWarmup = 48;

    public void Dispose()
    {
        _det.Dispose();
        _rec.Dispose();
    }

    private static OcrPoint Clamp(OcrPoint point, float scaleX, float scaleY, int width, int height) => new(
        Math.Clamp(point.X * scaleX, 0, width - 1),
        Math.Clamp(point.Y * scaleY, 0, height - 1));
}
