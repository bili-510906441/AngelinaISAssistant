using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AngelinaISAssistant;
using AngelinaISAssistant.Live;
using AngelinaISAssistant.Live.Intel;
using AngelinaISAssistant.Live.Ocr;

internal static class Program
{
    private const int ImageWidth = 1600;
    private const int ImageHeight = 900;

    private static readonly (string Text, double X, double Y, double Size)[] Lines =
    {
        ("安洁莉娜集成战略小助手", 80, 90, 52),
        ("HP 12345 / 67890", 80, 230, 46),
        ("Stage 3-8 源石锭 x12", 80, 380, 46),
        ("OCR TEST 2026", 80, 540, 46)
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var useDml = args.Any(arg =>
                string.Equals(arg, "--dml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--gpu", StringComparison.OrdinalIgnoreCase));
            var imageIndex = Array.FindIndex(
                args,
                arg => string.Equals(arg, "--image", StringComparison.OrdinalIgnoreCase));
            var imagePath = imageIndex >= 0 && imageIndex + 1 < args.Length ? args[imageIndex + 1] : null;
            var stageArgument = GetArgument(args, "--stage");
            var difficultyArgument = GetArgument(args, "--difficulty");
            var layerArgument = GetArgument(args, "--layer");
            var modelArgument = args.FirstOrDefault(arg =>
                !arg.StartsWith("--", StringComparison.Ordinal)
                && !string.Equals(arg, imagePath, StringComparison.Ordinal)
                && !string.Equals(arg, stageArgument, StringComparison.Ordinal)
                && !string.Equals(arg, difficultyArgument, StringComparison.Ordinal)
                && !string.Equals(arg, layerArgument, StringComparison.Ordinal));
            var modelDirectory = !string.IsNullOrWhiteSpace(modelArgument)
                ? modelArgument
                : Path.Combine(AppContext.BaseDirectory, "models", "ppocr");

            Console.WriteLine($"OCR TEST · models={modelDirectory}" + (imagePath is null ? string.Empty : $" · image={imagePath}"));

            foreach (var name in new[] { PpOcrEngine.DetFileName, PpOcrEngine.RecFileName, PpOcrEngine.KeysFileName })
            {
                var path = Path.Combine(modelDirectory, name);
                Console.WriteLine($"  {name}: exists={File.Exists(path)} size={(File.Exists(path) ? new FileInfo(path).Length : 0)}");
            }

            if (!PpOcrEngine.ModelFilesPresent(modelDirectory))
            {
                Console.WriteLine("OCR TEST FAIL: model files missing");
                return 1;
            }

            var (pixels, width, height) = RenderTestImage();
            Console.WriteLine($"rendered image {width}x{height}, bytes={pixels.Length}");

            if (useDml)
            {
                Console.WriteLine(
                    $"dml runtime files={OrtNative.DmlRuntimeFilesPresent} dmlAvailable={OrtNative.DmlAvailable} " +
                    $"dmlRuntime={OrtNative.IsDmlRuntime} runtime={OrtNative.RuntimeKind} version={OrtNative.Version}");

                foreach (var modelName in new[] { PpOcrEngine.DetFileName, PpOcrEngine.RecFileName })
                {
                    try
                    {
                        var direct = OrtNative.CreateSession(
                            Path.Combine(modelDirectory, modelName),
                            4,
                            OrtExecutionProvider.Dml,
                            0);
                        Console.WriteLine($"direct dml session {modelName}: ok");
                        OrtNative.ReleaseSession(direct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"direct dml session {modelName}: FAILED · {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            var loadWatch = Stopwatch.StartNew();
            using var engine = PpOcrEngine.Load(
                modelDirectory,
                useDml ? OrtExecutionProvider.Dml : OrtExecutionProvider.Cpu,
                0);
            loadWatch.Stop();
            Console.WriteLine(
                $"engine loaded in {loadWatch.ElapsedMilliseconds} ms · provider={engine.Provider} · " +
                $"dmlAvailable={OrtNative.DmlAvailable} · gpuRuntime={OrtNative.UsesGpuRuntime} · ort={OrtNative.Version}" + 
                (engine.IsGpu ? $" · device={engine.DeviceId}" : string.Empty));

            if (useDml)
            {
                var warmWatch = Stopwatch.StartNew();
                engine.Warmup();
                warmWatch.Stop();
                Console.WriteLine($"gpu warmup: {warmWatch.ElapsedMilliseconds} ms");
            }

            if (imagePath is not null)
            {
                RunImageDiagnostic(engine, imagePath, stageArgument, difficultyArgument, layerArgument);
                return 0;
            }

            var runWatch = Stopwatch.StartNew();
            var lines = engine.Recognize(pixels, width, height);
            runWatch.Stop();

            Console.WriteLine($"recognize: {lines.Count} line(s) in {runWatch.ElapsedMilliseconds} ms");

            if (useDml)
            {
                for (var pass = 2; pass <= 3; pass++)
                {
                    var warmWatch = Stopwatch.StartNew();
                    var warmLines = engine.Recognize(pixels, width, height);
                    warmWatch.Stop();
                    Console.WriteLine($"gpu recognize pass {pass}: {warmLines.Count} line(s) in {warmWatch.ElapsedMilliseconds} ms");
                }
            }

            var builder = new StringBuilder();

            foreach (var line in lines)
            {
                Console.WriteLine(
                    $"  [{line.Confidence:F2}] ({line.X:F0},{line.Y:F0},{line.Width:F0}x{line.Height:F0}) {line.Text}");
                builder.AppendLine(line.Text);
            }

            var text = builder.ToString();
            var expected = new[] { "安洁莉娜", "12345", "源石锭", "OCR" };

            foreach (var item in expected)
            {
                Console.WriteLine($"  contains '{item}': {text.Contains(item, StringComparison.OrdinalIgnoreCase)}");
            }

            var ok = text.Contains("OCR", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("12345", StringComparison.Ordinal)
                     || text.Contains("安洁莉娜", StringComparison.Ordinal);

            if (!ok)
            {
                Console.WriteLine("OCR TEST FAIL: no expected text was recognized");
                return 1;
            }

            if (!RunBackendTest(pixels, width, height))
            {
                Console.WriteLine("OCR TEST FAIL: live assist backend output check failed");
                return 1;
            }

            if (!RunRunInfoTests())
            {
                Console.WriteLine("OCR TEST FAIL: run info interpreter check failed");
                return 1;
            }

            if (!RunIntelTests())
            {
                Console.WriteLine("OCR TEST FAIL: intel (stage name / difficulty / enemy stats) check failed");
                return 1;
            }

            if (!RunBackendHintTest())
            {
                Console.WriteLine("OCR TEST FAIL: ocr -> run info end-to-end check failed");
                return 1;
            }

            if (!RunBackendIntelTest())
            {
                Console.WriteLine("OCR TEST FAIL: ocr -> enemy intel end-to-end check failed");
                return 1;
            }

            if (!RunCoordinatorTests())
            {
                Console.WriteLine("OCR TEST FAIL: coordinator behaviour check failed");
                return 1;
            }

            Console.WriteLine("OCR TEST OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("OCR TEST FAIL: " + ex);
            return 1;
        }
    }

    /// <summary>诊断模式：识别一张真实截图，打印每行文字的位置/置信度；可选模拟「敌方情报」页计算。</summary>
    private static void RunImageDiagnostic(
        PpOcrEngine engine,
        string path,
        string? stageArgument,
        string? difficultyArgument,
        string? layerArgument)
    {
        var (imagePixels, imageWidth, imageHeight) = LoadImage(path);
        Console.WriteLine($"image {imageWidth}x{imageHeight}");

        var watch = Stopwatch.StartNew();
        var imageLines = engine.Recognize(imagePixels, imageWidth, imageHeight);
        watch.Stop();

        Console.WriteLine($"recognize: {imageLines.Count} line(s) in {watch.ElapsedMilliseconds} ms");

        foreach (var line in imageLines)
        {
            var centerX = (line.X + line.Width / 2) / imageWidth;
            var centerY = (line.Y + line.Height / 2) / imageHeight;
            Console.WriteLine(
                $"  [{line.Confidence:F2}] ({line.X:F0},{line.Y:F0},{line.Width:F0}x{line.Height:F0}) " +
                $"c=({centerX:P0},{centerY:P0}) {line.Text}");

            foreach (var match in IsIntelData.FindStages(line.Text))
            {
                Console.WriteLine($"      stage -> {match.Display} ({match.LevelId}, {match.Topic})");
            }
        }

        // 难度徽章探针（与正式后端相同的相对区域与放大倍数）
        var (difficultyPixels, difficultyWidth, difficultyHeight) = OcrRegion.CropScaled(
            imagePixels, imageWidth, imageHeight, 0.0, 0.88, 0.10, 1.0, 3.0);
        var difficultyWatch = Stopwatch.StartNew();
        var difficultyLines = engine.Recognize(difficultyPixels, difficultyWidth, difficultyHeight);
        difficultyWatch.Stop();

        Console.WriteLine(
            $"difficulty probe ({difficultyWidth}x{difficultyHeight}, {difficultyWatch.ElapsedMilliseconds} ms): " +
            (DifficultyReader.TryParse(difficultyLines, out var difficulty) ? $"难度 {difficulty}" : "未识别"));

        foreach (var line in difficultyLines)
        {
            Console.WriteLine($"  [{line.Confidence:F2}] ({line.X:F0},{line.Y:F0},{line.Width:F0}x{line.Height:F0}) {line.Text}");
        }

        if (stageArgument is null)
        {
            return;
        }

        var stages = IsIntelData.FindStages(stageArgument);
        int? difficultyValue = int.TryParse(difficultyArgument, out var parsedDifficulty) ? parsedDifficulty : null;
        int? layerValue = int.TryParse(layerArgument, out var parsedLayer) ? parsedLayer : null;

        var nodeMode = StageNodeMode.Detect(
            imageLines,
            imageWidth,
            imageHeight,
            stages.Count > 0 ? stages[0].Display : stageArgument);
        var nodeText = nodeMode is null ? "unknown" : nodeMode.Value ? "紧急作战" : "作战";

        Console.WriteLine(
            $"intel simulate: stage='{stageArgument}' matches={stages.Count} difficulty={difficultyValue} " +
            $"layer={layerValue} node={nodeText}");

        var reader = new EnemyIntelReader();
        var annotations = reader.Update(imageLines, imageWidth, imageHeight, stages, difficultyValue, layerValue, nodeMode ?? false);

        Console.WriteLine($"  enemy={reader.LastEnemyKey ?? "?"} {FormatStatValues(reader.LastStatResult)}");
        Console.WriteLine($"  annotations={annotations.Count}");

        foreach (var annotation in annotations)
        {
            Console.WriteLine(
                $"    '{annotation.Text}' x={annotation.X:F0} y={annotation.Y:F0} " +
                $"size={annotation.FontSize:F0} maxW={annotation.MaxWidth:F0} right={annotation.AlignRight}");
        }
    }

    private static bool MatchesStats(EnemyStatResult? result, double hp, double atk, double def, double res, double ms) =>
        result is not null
        && Math.Abs(result.Values.Hp - hp) < 0.001
        && Math.Abs(result.Values.Atk - atk) < 0.001
        && Math.Abs(result.Values.Def - def) < 0.001
        && Math.Abs(result.Values.Res - res) < 0.001
        && Math.Abs(result.Values.Ms - ms) < 0.001;

    private static string FormatStatValues(EnemyStatResult? result) =>
        result is null
            ? "values=-"
            : $"enemy={result.EnemyKey} values: hp={result.Values.Hp} atk={result.Values.Atk} def={result.Values.Def} " +
              $"res={result.Values.Res} ms={result.Values.Ms} (difficulty={result.Difficulty}, layer={result.Layer})";

    private static string? GetArgument(string[] args, string name)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static (byte[] Pixels, int Width, int Height) LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var imagePixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(imagePixels, stride, 0);
        return (imagePixels, converted.PixelWidth, converted.PixelHeight);
    }

    private static (byte[] Pixels, int Width, int Height) RenderTestImage()
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, ImageWidth, ImageHeight));

            var typeface = new Typeface("Microsoft YaHei UI");

            foreach (var line in Lines)
            {
                var formatted = new FormattedText(
                    line.Text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    line.Size,
                    Brushes.Black,
                    1.0);

                context.DrawText(formatted, new Point(line.X, line.Y));
            }
        }

        var bitmap = new RenderTargetBitmap(ImageWidth, ImageHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = ImageWidth * 4;
        var pixels = new byte[stride * ImageHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        return (pixels, ImageWidth, ImageHeight);
    }

    /// <summary>验证正式后端不再输出原始文字/框/置信度；测试图里没有主题/层数提示时结果应为空。</summary>
    /// <summary>渲染一张带主题特征和层数提示的图，验证 OCR → RunInfoInterpreter 端到端。</summary>
    private static (byte[] Pixels, int Width, int Height) RenderHintImage()
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, ImageWidth, ImageHeight));
            var typeface = new Typeface("Microsoft YaHei UI");

            void Draw(string text, double x, double y, double size)
            {
                var formatted = new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    size,
                    Brushes.Black,
                    1.0);
                context.DrawText(formatted, new Point(x, y));
            }

            Draw("2层 甜美的伤口", 600, 60, 44);
            Draw("猎犬病原", 300, 220, 44);
            Draw("零件箱 总估价", 240, 800, 40);
        }

        var bitmap = new RenderTargetBitmap(ImageWidth, ImageHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = ImageWidth * 4;
        var pixels = new byte[stride * ImageHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return (pixels, ImageWidth, ImageHeight);
    }

    /// <summary>端到端验证：真实 OCR 结果应被解释成关卡名 + 主题/层数，且遮罩只收到语义提示行。</summary>
    private static bool RunBackendHintTest()
    {
        Console.WriteLine("backend hint test:");

        using var backend = new PpOcrLiveAssistBackend();

        if (!backend.IsAvailable)
        {
            return false;
        }

        backend.Initialize();
        backend.ResetRunInfo();

        var (hintPixels, hintWidth, hintHeight) = RenderHintImage();
        var result = backend.Process(new CapturedFrame(hintPixels, hintWidth, hintHeight), LiveAssistOptions.None);
        var items = result?.Items ?? Array.Empty<OverlayItem>();

        foreach (var item in items)
        {
            Console.WriteLine($"  [{item.Kind}] {item.Text}");
        }

        return items.Count == 2
               && items[0].Text == "识别到猎犬病原"
               && items[1].Text == "已进入沉沦者的黑流树海第2层（甜美的伤口）";
    }

    /// <summary>渲染一张同时包含「地图界面 + 敌方情报页」特征的图，验证后端完整链路（关卡 / 难度 / 层数 / 敌人数值）。</summary>
    private static (byte[] Pixels, int Width, int Height) RenderIntelImage(bool emergency = false)
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, ImageWidth, ImageHeight));
            var typeface = new Typeface("Microsoft YaHei UI");

            void Draw(string text, double x, double y, double size)
            {
                var formatted = new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    size,
                    Brushes.Black,
                    1.0);
                context.DrawText(formatted, new Point(x, y));
            }

            // 地图界面：层名（顶部中间）、节点类型 / 关卡名（右上）、底部主题特征、左下难度徽章
            Draw("玻利瓦尔肤层", 640, 20, 30);

            if (emergency)
            {
                Draw("紧急作战", 1150, 70, 26);
            }
            else
            {
                Draw("作战", 1150, 70, 26);
            }

            Draw("急不可耐", 1150, 120, 34);
            Draw("零件箱", 1100, 850, 24);
            Draw("总估价", 800, 850, 24);
            Draw("9", 40, 800, 44);

            // 敌方情报页：分类标签 + 当前敌人 + 五个属性标签与字母等级
            Draw("全部", 900, 30, 26);
            Draw("精英", 1000, 30, 26);
            Draw("领袖", 1100, 30, 26);
            Draw("内测版自助出餐终端", 900, 150, 36);

            Draw("生命值", 830, 300, 24);
            Draw("攻击力", 830, 380, 24);
            Draw("防御力", 830, 460, 24);
            Draw("法术抗性", 830, 540, 24);
            Draw("移动速度", 1140, 300, 24);
            Draw("攻击速度", 1140, 380, 24);
            Draw("元素抗性", 1140, 460, 24);
            Draw("损伤抵抗", 1140, 540, 24);

            Draw("B", 1040, 300, 26);
            Draw("B+", 1040, 380, 26);
            Draw("A", 1040, 460, 26);
            Draw("B+", 1040, 540, 26);
            Draw("C", 1420, 300, 26);
            Draw("B+", 1420, 380, 26);
            Draw("E", 1420, 460, 26);
            Draw("E", 1420, 540, 26);
        }

        var bitmap = new RenderTargetBitmap(ImageWidth, ImageHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = ImageWidth * 4;
        var pixels = new byte[stride * ImageHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var debugPath = Path.Combine(Path.GetTempPath(), "aisa_intel_test.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(debugPath))
        {
            encoder.Save(stream);
        }

        Console.WriteLine($"  (debug image: {debugPath})");
        return (pixels, ImageWidth, ImageHeight);
    }

    /// <summary>
    /// 端到端验证：一帧里同时出现地图与敌方情报特征时，后端应输出关卡/难度/层数提示 + 8 条数值标注；
    /// 「作战」与「紧急作战」分别使用 n_mods / elite_mods，数值不同。
    /// </summary>
    private static bool RunBackendIntelTest()
    {
        Console.WriteLine("backend intel test:");

        using var backend = new PpOcrLiveAssistBackend();

        if (!backend.IsAvailable)
        {
            return false;
        }

        backend.Initialize();

        var expectedMessages = new[]
        {
            "识别到急不可耐",
            "识别到难度9",
            "已进入沉沦者的黑流树海第1层（玻利瓦尔肤层）"
        };

        var normalOk = ProcessIntelFrame(
            backend,
            RenderIntelImage(false),
            expectedMessages,
            "8268,975,1000,40,0.6,2,0,0",
            "作战");

        var eliteOk = ProcessIntelFrame(
            backend,
            RenderIntelImage(true),
            expectedMessages,
            "9922,1170,1200,40,0.6,2,0,0",
            "紧急作战");

        return normalOk && eliteOk;
    }

    private static bool ProcessIntelFrame(
        PpOcrLiveAssistBackend backend,
        (byte[] Pixels, int Width, int Height) image,
        string[] expectedMessages,
        string expectedAnnotations,
        string label)
    {
        backend.ResetRunInfo();

        var result = backend.Process(new CapturedFrame(image.Pixels, image.Width, image.Height), LiveAssistOptions.None);
        var items = result?.Items ?? Array.Empty<OverlayItem>();
        var annotations = result?.Annotations ?? Array.Empty<OverlayAnnotation>();
        var messages = items.Select(item => item.Text).ToArray();
        var annotationText = string.Join(",", annotations.Select(item => item.Text));

        Console.WriteLine($"  [{label}] messages=[{string.Join(" | ", messages)}] annotations=[{annotationText}]");

        var ok = messages.SequenceEqual(expectedMessages)
                 && annotations.Count == 8
                 && annotationText == expectedAnnotations;

        if (!ok)
        {
            Console.WriteLine($"  [{label}] FAILED");
        }

        return ok;
    }

    private static bool RunBackendTest(byte[] pixels, int width, int height)
    {
        Console.WriteLine("backend test: PpOcrLiveAssistBackend");

        using var backend = new PpOcrLiveAssistBackend();
        Console.WriteLine($"  available={backend.IsAvailable}");

        if (!backend.IsAvailable)
        {
            return false;
        }

        backend.Initialize();
        backend.ResetRunInfo();
        Console.WriteLine($"  onnxruntime={OrtNative.Version}");

        var result = backend.Process(new CapturedFrame(pixels, width, height), LiveAssistOptions.None);
        var items = result?.Items ?? Array.Empty<OverlayItem>();

        Console.WriteLine($"  items={items.Count} (rendered test image has no theme/layer hints)");

        foreach (var item in items)
        {
            Console.WriteLine($"  [{item.Kind}] {item.Text}");
        }

        // 新版遮罩只显示主题/层数；测试图没有这些提示，必须为空（不再回退到原始 OCR 文字）
        return items.Count == 0;
    }

    /// <summary>验证主题/层数解释器、关卡名识别与遮罩文案格式。</summary>
    private static bool RunRunInfoTests()
    {
        Console.WriteLine("run info tests:");
        var failed = false;

        var black = new RunInfoInterpreter();
        var blackInfo = black.Update(
            new[]
            {
                new OcrLine("2层 甜美的伤口", 0.99f, 800, 80, 320, 50),
                new OcrLine("零件箱 总估价", 0.98f, 300, 1000, 600, 40)
            },
            1920,
            1080);

        var blackMessage = blackInfo is null ? null : RunInfoInterpreter.BuildOverlayMessage(blackInfo);
        Console.WriteLine($"  black: theme={blackInfo?.Theme} layer={blackInfo?.Layer} name={blackInfo?.LayerName}");
        Console.WriteLine($"  black message: {blackMessage}");

        if (blackInfo is null
            || blackInfo.Theme != "沉沦者的黑流树海"
            || blackInfo.Layer != 2
            || blackInfo.LayerName != "甜美的伤口"
            || blackInfo.Stage is not null
            || blackMessage != "已进入沉沦者的黑流树海第2层（甜美的伤口）")
        {
            failed = true;
        }

        // 没有新提示时应保留上一次识别结果
        var sticky = black.Update(Array.Empty<OcrLine>(), 1920, 1080);

        if (sticky is null || sticky.Theme != blackInfo?.Theme || sticky.Layer != blackInfo?.Layer)
        {
            Console.WriteLine("  sticky state: FAILED");
            failed = true;
        }

        var garden = new RunInfoInterpreter();
        var gardenInfo = garden.Update(
            new[]
            {
                new OcrLine("3层 云瓦亭", 0.99f, 820, 90, 280, 50),
                new OcrLine("钱盒", 0.98f, 900, 1010, 200, 40)
            },
            1920,
            1080);

        var gardenMessage = gardenInfo is null ? null : RunInfoInterpreter.BuildOverlayMessage(gardenInfo);
        Console.WriteLine($"  garden: theme={gardenInfo?.Theme} layer={gardenInfo?.Layer} name={gardenInfo?.LayerName}");
        Console.WriteLine($"  garden message: {gardenMessage}");

        if (gardenInfo is null
            || gardenInfo.Theme != "岁的界园志异"
            || gardenInfo.Layer != 3
            || gardenInfo.LayerName != "云瓦亭"
            || gardenMessage != "已进入岁的界园志异第3层（云瓦亭）")
        {
            failed = true;
        }

        garden.Reset();

        if (garden.Update(Array.Empty<OcrLine>(), 1920, 1080) is not null)
        {
            Console.WriteLine("  reset state: FAILED");
            failed = true;
        }

        // 关卡名：地图界面（eg3 同款）里识别到关卡名，并保存 levelId 供敌方情报页使用
        var stageInterpreter = new RunInfoInterpreter();
        var stageInfo = stageInterpreter.Update(
            new[]
            {
                new OcrLine("玻利瓦尔肤层", 1.00f, 1144, 10, 272, 52),
                new OcrLine("急不可耐", 1.00f, 1899, 260, 232, 64),
                new OcrLine("零件箱", 1.00f, 1554, 1473, 98, 41),
                new OcrLine("总估价", 1.00f, 1126, 1474, 97, 38)
            },
            2560,
            1600);

        var stageMatches = stageInterpreter.GetStageMatches();
        var stageMessage = stageInfo?.Stage is null ? null : $"识别到{stageInfo.Stage.Display}";
        Console.WriteLine(
            $"  stage: name={stageInfo?.Stage?.Display} level={stageInfo?.Stage?.LevelId} " +
            $"topic={stageInfo?.Stage?.Topic} matches={stageMatches.Count} message={stageMessage}");

        if (stageInfo?.Stage is null
            || stageInfo.Stage.Display != "急不可耐"
            || stageInfo.Stage.LevelId != "level_rogue6_1-3"
            || stageInfo.Stage.Topic != "rogue_6"
            || stageMatches.Count != 1
            || stageInfo.Theme != "沉沦者的黑流树海"
            || stageInfo.Layer != 1
            || stageMessage != "识别到急不可耐")
        {
            Console.WriteLine("  stage detection: FAILED");
            failed = true;
        }

        // 置信度 0.6 门槛：低置信度的关卡名不认
        var lowConfidence = new RunInfoInterpreter();
        var lowInfo = lowConfidence.Update(
            new[] { new OcrLine("急不可耐", 0.5f, 1899, 260, 232, 64) },
            2560,
            1600);

        if (lowInfo?.Stage is not null)
        {
            Console.WriteLine("  stage confidence threshold: FAILED");
            failed = true;
        }

        return !failed;
    }

    /// <summary>验证关卡名（中/英/日、标点、变体名归一化）、难度数字与敌方情报页数值。</summary>
    private static bool RunIntelTests()
    {
        Console.WriteLine("intel tests:");
        var failed = false;

        Console.WriteLine($"  data available: {IsIntelData.IsAvailable}");

        if (!IsIntelData.IsAvailable)
        {
            Console.WriteLine("  intel data: FAILED");
            return false;
        }

        var stageCases = new (string Text, string Display, string LevelId, string Topic)[]
        {
            ("急不可耐", "急不可耐", "level_rogue6_1-3", "rogue_6"),
            ("ISW-NO 急不可耐", "急不可耐", "level_rogue6_1-3", "rogue_6"),
            ("所守者，义", "所守者，义", "level_rogue5_2-3", "rogue_yan"),
            ("所守者义", "所守者，义", "level_rogue5_2-3", "rogue_yan"),
            ("谤天（单）", "谤天", "level_rogue5_fs-1b", "rogue_yan"),
            ("Mind the Doors", "Mind the Doors", "level_rogue1_b-1", "rogue_phantom"),
            ("ISW-DF Mind the Doors", "Mind the Doors", "level_rogue1_b-1", "rogue_phantom"),
            ("敷居に注意を", "敷居に注意を", "level_rogue1_b-1", "rogue_phantom"),
            ("剑·刀·矛", "剑·刀·矛", "level_rogue5_b-2", "rogue_yan")
        };

        foreach (var (text, display, levelId, topic) in stageCases)
        {
            var matched = IsIntelData.TryMatchStage(text, out var match);
            var actual = matched ? $"{match.Display}/{match.LevelId}/{match.Topic}" : "null";
            Console.WriteLine($"  stage '{text}' -> {actual}");

            if (!matched || match.Display != display || match.LevelId != levelId || match.Topic != topic)
            {
                Console.WriteLine("    FAILED");
                failed = true;
            }
        }

        var noStageCases = new[] { "甜美的伤口", "安洁莉娜", "零件箱 总估价", "" };

        foreach (var text in noStageCases)
        {
            if (IsIntelData.TryMatchStage(text, out _))
            {
                Console.WriteLine($"  stage '{text}' should NOT match: FAILED");
                failed = true;
            }
        }

        // 难度数字 0~18，置信度门槛 0.6
        var difficultyCases = new (float Confidence, string Text, int? Expected)[]
        {
            (1.00f, "9", 9),
            (0.95f, "18", 18),
            (0.80f, "0", 0),
            (1.00f, "19", null),
            (0.55f, "9", null),
            (0.90f, "9/12", null)
        };

        foreach (var (confidence, text, expected) in difficultyCases)
        {
            var parsed = DifficultyReader.TryParse(new[] { new OcrLine(text, confidence, 0, 0, 0, 0) }, out var difficulty);
            var actual = parsed ? difficulty : -1;
            Console.WriteLine($"  difficulty '{text}' ({confidence:F2}) -> {(parsed ? actual.ToString() : "null")}");

            if (actual != (expected ?? -1))
            {
                Console.WriteLine("    FAILED");
                failed = true;
            }
        }

        // 真实截图（eg1/eg2）的 OCR 结果：敌方情报页 -> 计算数值 -> 生成 5 条标注
        var eg1Reader = new EnemyIntelReader();
        var eg1Annotations = eg1Reader.Update(
            Eg1IntelLines(),
            2560,
            1600,
            IsIntelData.FindStages("急不可耐"),
            9,
            1,
            false);

        Console.WriteLine(
            $"  eg1: enemy={eg1Reader.LastEnemyKey} values={FormatStatValues(eg1Reader.LastStatResult)} " +
            $"annotations=[{string.Join(", ", eg1Annotations.Select(item => item.Text))}]");

        if (eg1Reader.LastEnemyKey != "enemy_10073_mpcar"
            || eg1Reader.LastStatResult is null
            || Math.Abs(eg1Reader.LastStatResult.Values.Hp - 2756) > 0.001
            || Math.Abs(eg1Reader.LastStatResult.Values.Atk - 318) > 0.001
            || eg1Annotations.Count != 8
            || string.Join(",", eg1Annotations.Select(item => item.Text)) != "2756,318,200,25,1.9,2,0,0")
        {
            Console.WriteLine("  eg1: FAILED");
            failed = true;
        }

        var eg2Reader = new EnemyIntelReader();
        var eg2Annotations = eg2Reader.Update(
            Eg2IntelLines(),
            2560,
            1600,
            IsIntelData.FindStages("急不可耐"),
            9,
            1,
            false);

        Console.WriteLine(
            $"  eg2: enemy={eg2Reader.LastEnemyKey} values={FormatStatValues(eg2Reader.LastStatResult)} " +
            $"annotations=[{string.Join(", ", eg2Annotations.Select(item => item.Text))}]");

        if (eg2Reader.LastEnemyKey != "enemy_10077_mpbarr"
            || eg2Reader.LastStatResult is null
            || Math.Abs(eg2Reader.LastStatResult.Values.Hp - 8268) > 0.001
            || Math.Abs(eg2Reader.LastStatResult.Values.Atk - 975) > 0.001
            || eg2Annotations.Count != 8
            || string.Join(",", eg2Annotations.Select(item => item.Text)) != "8268,975,1000,40,0.6,2,0,0")
        {
            Console.WriteLine("  eg2: FAILED");
            failed = true;
        }

        // 作战 / 紧急作战：节点类型识别（eg3 / eg4 的 OCR 结果）
        var normalMap = new[]
        {
            new OcrLine("作战", 1.00f, 1900, 218, 77, 42),
            new OcrLine("急不可耐", 1.00f, 1899, 260, 232, 64)
        };
        var eliteMap = new[]
        {
            new OcrLine("紧急作战", 1.00f, 1902, 217, 142, 43),
            new OcrLine("灌水贤者", 1.00f, 1898, 258, 230, 64),
            new OcrLine("作战", 1.00f, 1108, 431, 87, 49),
            new OcrLine("紧急作战", 1.00f, 1071, 1200, 159, 50)
        };
        var normalMode = StageNodeMode.Detect(normalMap, 2560, 1600, "急不可耐");
        var eliteModeDetected = StageNodeMode.Detect(eliteMap, 2560, 1600, "灌水贤者");
        var unknownMode = StageNodeMode.Detect(
            new[] { new OcrLine("急不可耐", 1.00f, 1899, 260, 232, 64) },
            2560,
            1600,
            "急不可耐");

        Console.WriteLine($"  node mode: 作战={normalMode} 紧急作战={eliteModeDetected} unknown={unknownMode}");

        if (normalMode != false || eliteModeDetected != true || unknownMode is not null)
        {
            Console.WriteLine("  node mode: FAILED");
            failed = true;
        }

        // 紧急作战：elite_mods + 敌人替换（炮手 -> 炮击组长）
        var normalMortar = EnemyStatCalculator.Compute(
            new EnemyStatContext("rogue_6", "level_rogue6_1-4", "enemy_1024_mortar", 9, 1, false));
        var eliteMortar = EnemyStatCalculator.Compute(
            new EnemyStatContext("rogue_6", "level_rogue6_1-4", "enemy_1024_mortar", 9, 1, true));
        var eliteShepherd = EnemyStatCalculator.Compute(
            new EnemyStatContext("rogue_6", "level_rogue6_1-4", "enemy_2134_shrshep", 9, 1, true));

        Console.WriteLine($"  作战 炮手: {FormatStatValues(normalMortar)}");
        Console.WriteLine($"  紧急作战 炮手->炮击组长: {FormatStatValues(eliteMortar)}");
        Console.WriteLine($"  紧急作战 颂水贤者: {FormatStatValues(eliteShepherd)}");

        if (!MatchesStats(normalMortar, 4547, 424, 150, 0, 0.8)
            || eliteMortar is null
            || eliteMortar.EnemyKey != "enemy_1024_mortar_2"
            || !MatchesStats(eliteMortar, 8268, 700, 180, 0, 0.8)
            || !MatchesStats(eliteShepherd, 13229, 509, 240, 50, 0.8))
        {
            Console.WriteLine("  elite stage calculation: FAILED");
            failed = true;
        }

        // 紧急作战下情报页里的「炮手」应换算成炮击组长（与 tomimi 的 enemy_replace 一致）
        var mortarViewLines = Eg1IntelLines()
            .Select(line => line.Text == "迷茫的修理小助手"
                ? new OcrLine("炮手", line.Confidence, line.X, line.Y, line.Width, line.Height)
                : line)
            .ToArray();

        var normalViewer = new EnemyIntelReader();
        normalViewer.Update(mortarViewLines, 2560, 1600, IsIntelData.FindStages("灌水贤者"), 9, 1, false);

        var eliteViewer = new EnemyIntelReader();
        var eliteViewAnnotations = eliteViewer.Update(
            mortarViewLines,
            2560,
            1600,
            IsIntelData.FindStages("灌水贤者"),
            9,
            1,
            true);

        Console.WriteLine(
            $"  紧急作战情报页 炮手 -> {eliteViewer.LastEnemyKey} " +
            $"[{string.Join(",", eliteViewAnnotations.Select(item => item.Text))}]");

        if (normalViewer.LastEnemyKey != "enemy_1024_mortar"
            || eliteViewer.LastEnemyKey != "enemy_1024_mortar_2"
            || string.Join(",", eliteViewAnnotations.Select(item => item.Text)) != "8268,700,180,0,0.8,4.5,0,0")
        {
            Console.WriteLine("  elite enemy replacement: FAILED");
            failed = true;
        }

        // 缺一个分类标签就不能当作敌方情报页
        var missingLabelReader = new EnemyIntelReader();
        var missingLabelAnnotations = missingLabelReader.Update(
            Eg1IntelLines().Where(line => line.Text != "领袖").ToArray(),
            2560,
            1600,
            IsIntelData.FindStages("急不可耐"),
            9,
            1,
            false);

        if (missingLabelAnnotations.Count != 0)
        {
            Console.WriteLine("  panel label requirement: FAILED");
            failed = true;
        }

        // 一帧漏检保留一帧、连续两帧消失后清空（避免数值闪烁）
        var graceReader = new EnemyIntelReader();
        graceReader.Update(Eg1IntelLines(), 2560, 1600, IsIntelData.FindStages("急不可耐"), 9, 1, false);
        var firstMiss = graceReader.Update(Array.Empty<OcrLine>(), 2560, 1600, IsIntelData.FindStages("急不可耐"), 9, 1, false);
        var secondMiss = graceReader.Update(Array.Empty<OcrLine>(), 2560, 1600, IsIntelData.FindStages("急不可耐"), 9, 1, false);

        if (firstMiss.Count != 8 || secondMiss.Count != 0)
        {
            Console.WriteLine($"  panel grace: FAILED (first={firstMiss.Count}, second={secondMiss.Count})");
            failed = true;
        }

        // 数字格式：万 / 科学计数法 / 完整数字
        var formats = new (double Value, NumberDisplayFormat Format, string Expected)[]
        {
            (358000, NumberDisplayFormat.Wan, "35.8w"),
            (358000, NumberDisplayFormat.Scientific, "3.58e5"),
            (358000, NumberDisplayFormat.Plain, "358000"),
            (2756, NumberDisplayFormat.Wan, "2756"),
            (1.9, NumberDisplayFormat.Plain, "1.9")
        };

        foreach (var (value, format, expected) in formats)
        {
            var actual = NumberDisplay.Format(value, format);
            Console.WriteLine($"  number {value} {format} -> {actual}");

            if (actual != expected)
            {
                Console.WriteLine("    FAILED");
                failed = true;
            }
        }

        // 显示格式设置会影响标注文本（万格式下 358000 -> 35.8w）
        var previousFormat = AngelinaISAssistant.AppSettings.Current.NumberFormat;
        AngelinaISAssistant.AppSettings.Current.NumberFormat = NumberDisplayFormat.Wan;

        try
        {
            var formatReader = new EnemyIntelReader();
            var wanAnnotations = formatReader.Update(Eg1IntelLines(), 2560, 1600, IsIntelData.FindStages("急不可耐"), 9, 1, false);

            if (wanAnnotations.Count != 8)
            {
                Console.WriteLine("  annotation format: FAILED");
                failed = true;
            }
        }
        finally
        {
            AngelinaISAssistant.AppSettings.Current.NumberFormat = previousFormat;
        }

        return !failed;
    }

    /// <summary>eg1 截图（2560x1600）的 OCR 结果：当前查看「迷茫的修理小助手」。</summary>
    private static OcrLine[] Eg1IntelLines() => new[]
    {
        new OcrLine("全部", 1.00f, 1664, 77, 84, 46),
        new OcrLine("精英", 1.00f, 1857, 76, 87, 49),
        new OcrLine("领袖", 1.00f, 2079, 75, 82, 51),
        new OcrLine("源石造物", 1.00f, 1376, 194, 171, 47),
        new OcrLine("迷茫的修理小助手", 1.00f, 1539, 250, 516, 67),
        new OcrLine("OG1", 0.99f, 1423, 265, 72, 42),
        new OcrLine("近战物理", 1.00f, 1377, 333, 197, 51),
        new OcrLine("十生命值", 0.99f, 1708, 436, 161, 43),
        new OcrLine("移动速度", 0.99f, 2106, 436, 182, 44),
        new OcrLine("D", 1.00f, 1978, 437, 44, 46),
        new OcrLine("S+", 1.00f, 2340, 430, 84, 58),
        new OcrLine("X攻击力", 1.00f, 1705, 527, 163, 43),
        new OcrLine("攻击速度", 0.98f, 2088, 525, 202, 48),
        new OcrLine("C", 1.00f, 1977, 522, 50, 54),
        new OcrLine("B+", 1.00f, 2338, 520, 86, 56),
        new OcrLine("防御力", 1.00f, 1748, 615, 120, 45),
        new OcrLine("元素抗性", 1.00f, 2129, 617, 160, 43),
        new OcrLine("C", 1.00f, 1976, 612, 52, 54),
        new OcrLine("E", 1.00f, 2361, 612, 46, 54),
        new OcrLine("法术抗性", 1.00f, 1751, 708, 155, 43),
        new OcrLine("损伤抵抗", 1.00f, 2129, 707, 160, 43),
        new OcrLine("B", 1.00f, 1979, 703, 43, 52),
        new OcrLine("E", 1.00f, 2361, 701, 46, 54),
        new OcrLine("关联敌人", 1.00f, 1703, 1205, 153, 46),
        new OcrLine("内测版自助出餐终端", 1.00f, 1703, 1268, 441, 49)
    };

    /// <summary>eg2 截图（2560x1600）的 OCR 结果：当前查看「内测版自助出餐终端」。</summary>
    private static OcrLine[] Eg2IntelLines() => new[]
    {
        new OcrLine("全部", 1.00f, 1662, 77, 85, 46),
        new OcrLine("精英", 1.00f, 1855, 74, 89, 52),
        new OcrLine("领袖", 1.00f, 2078, 77, 83, 48),
        new OcrLine("源石造物", 1.00f, 1376, 194, 171, 47),
        new OcrLine("内测版自助出餐终端", 1.00f, 1544, 252, 577, 68),
        new OcrLine("OG9", 0.99f, 1422, 266, 76, 40),
        new OcrLine("近战物理", 1.00f, 1377, 333, 197, 51),
        new OcrLine("十生命值", 0.99f, 1708, 436, 161, 43),
        new OcrLine("移动速度", 0.99f, 2106, 436, 182, 44),
        new OcrLine("B", 1.00f, 1979, 434, 45, 50),
        new OcrLine("C", 1.00f, 2361, 431, 50, 54),
        new OcrLine("X攻击力", 1.00f, 1704, 524, 165, 47),
        new OcrLine("攻击速度", 0.98f, 2088, 525, 202, 48),
        new OcrLine("B+", 1.00f, 1956, 520, 84, 56),
        new OcrLine("B+", 1.00f, 2338, 520, 86, 56),
        new OcrLine("防御力", 1.00f, 1747, 615, 121, 48),
        new OcrLine("元素抗性", 1.00f, 2129, 617, 160, 43),
        new OcrLine("A", 1.00f, 1973, 612, 52, 54),
        new OcrLine("E", 1.00f, 2361, 612, 46, 54),
        new OcrLine("法术抗性", 1.00f, 1751, 708, 155, 43),
        new OcrLine("损伤抵抗", 1.00f, 2127, 707, 161, 43),
        new OcrLine("B+", 1.00f, 1957, 701, 83, 53),
        new OcrLine("E", 1.00f, 2361, 701, 46, 54),
        new OcrLine("关联敌人", 1.00f, 1703, 1330, 153, 43),
        new OcrLine("迷茫的修理小助手", 1.00f, 1699, 1387, 398, 58)
    };

    /// <summary>
    /// 验证协调器：无勾选项时仍处理（测试模式）、停止后丢弃晚到的识别结果。
    /// </summary>
    private static bool RunCoordinatorTests()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;

        // 1. 测试模式：没有任何勾选项也应该跑后端
        var backend = new FakeLiveBackend { DelayMs = 0 };
        var events = new List<LiveAssistResult?>();
        var handled = false;

        using (var coordinator = new LiveAssistCoordinator(
                   backend,
                   () => LiveAssistOptions.None,
                   dispatcher,
                   TimeSpan.Zero,
                   processWithoutOptions: true))
        {
            coordinator.ResultsReady += result =>
            {
                events.Add(result);

                if (result is { Items.Count: > 0 })
                {
                    handled = true;
                }
            };

            coordinator.OfferFrame(new byte[32 * 16 * 4], 32, 16);
            Pump(TimeSpan.FromMilliseconds(800));
        }

        Console.WriteLine($"coordinator: processWithoutOptions calls={backend.ProcessCalls} handled={handled}");

        if (backend.ProcessCalls != 1 || !handled)
        {
            return false;
        }

        // 2. 识别进行中停止（Clear）：晚到的结果必须被丢弃
        var slow = new FakeLiveBackend { DelayMs = 400 };
        var lateEvents = new List<LiveAssistResult?>();
        var stale = false;

        using (var coordinator = new LiveAssistCoordinator(
                   slow,
                   () => LiveAssistOptions.None,
                   dispatcher,
                   TimeSpan.Zero,
                   processWithoutOptions: true))
        {
            coordinator.ResultsReady += result =>
            {
                lateEvents.Add(result);

                if (result is { Items.Count: > 0 })
                {
                    stale = true;
                }
            };

            coordinator.OfferFrame(new byte[32 * 16 * 4], 32, 16);
            Thread.Sleep(100);
            coordinator.Clear(); // 等价于停止采集
            Pump(TimeSpan.FromMilliseconds(900));
        }

        Console.WriteLine($"coordinator: staleResultAfterClear={stale} events={lateEvents.Count}");

        if (stale)
        {
            return false;
        }

        // 3. 回退模式：未开启 processWithoutOptions 时，没有勾选就不跑后端
        var idle = new FakeLiveBackend { DelayMs = 0 };

        using (var coordinator = new LiveAssistCoordinator(
                   idle,
                   () => LiveAssistOptions.None,
                   dispatcher,
                   TimeSpan.Zero,
                   processWithoutOptions: false))
        {
            coordinator.OfferFrame(new byte[32 * 16 * 4], 32, 16);
            Pump(TimeSpan.FromMilliseconds(400));
        }

        Console.WriteLine($"coordinator: fallback no-options calls={idle.ProcessCalls}");

        // 4. 忙时到达的最新帧不能被丢弃：窗口最小化等场景只变化一两帧，
        //    如果这些帧在忙时被丢掉，遮罩会一直保留旧文字。
        var pending = new FakeLiveBackend { DelayMs = 300 };
        using (var coordinator = new LiveAssistCoordinator(
                   pending,
                   () => LiveAssistOptions.None,
                   dispatcher,
                   TimeSpan.Zero,
                   processWithoutOptions: true))
        {
            coordinator.OfferFrame(new byte[] { 1 }, 32, 16);
            Thread.Sleep(50);
            coordinator.OfferFrame(new byte[] { 2 }, 32, 16);
            Pump(TimeSpan.FromMilliseconds(1200));
        }

        Console.WriteLine($"coordinator: busy-latest-frame calls={pending.ProcessCalls}");

        if (pending.ProcessCalls < 2)
        {
            return false;
        }

        // 5. 旧帧返回文字、忙时到来的新帧没有文字：最终必须用空结果清掉遮罩
        var clearing = new FakeLiveBackend { DelayMs = 300, EmptyWhenFirstByteZero = true };
        var clearResults = new List<LiveAssistResult?>();

        using (var coordinator = new LiveAssistCoordinator(
                   clearing,
                   () => LiveAssistOptions.None,
                   dispatcher,
                   TimeSpan.Zero,
                   processWithoutOptions: true))
        {
            coordinator.ResultsReady += result => clearResults.Add(result);
            coordinator.OfferFrame(new byte[] { 1 }, 32, 16);
            Thread.Sleep(50);
            coordinator.OfferFrame(new byte[] { 0 }, 32, 16);
            Pump(TimeSpan.FromMilliseconds(1200));
        }

        var lastItems = clearResults.LastOrDefault()?.Items.Count ?? -1;
        Console.WriteLine($"coordinator: stale-clear calls={clearing.ProcessCalls} lastItems={lastItems}");

        if (clearing.ProcessCalls < 2 || lastItems != 0)
        {
            return false;
        }

        return idle.ProcessCalls == 0;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class FakeLiveBackend : ILiveAssistBackend
    {
        private int _calls;

        public int DelayMs { get; init; }

        public bool EmptyWhenFirstByteZero { get; init; }

        public int ProcessCalls => Volatile.Read(ref _calls);

        public bool IsAvailable => true;

        public void Initialize()
        {
        }

        public LiveAssistResult? Process(CapturedFrame frame, LiveAssistOptions options)
        {
            Interlocked.Increment(ref _calls);

            if (DelayMs > 0)
            {
                Thread.Sleep(DelayMs);
            }

            if (EmptyWhenFirstByteZero && frame.Pixels.Length > 0 && frame.Pixels[0] == 0)
            {
                return new LiveAssistResult(Array.Empty<OverlayItem>());
            }

            return new LiveAssistResult(new[]
            {
                new OverlayItem(OverlayKind.Info, "fake 0.99", 1, 2, 30, 20)
            });
        }

        public void Dispose()
        {
        }
    }
}
