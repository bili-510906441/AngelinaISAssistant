using System.Collections.Generic;
using System.Globalization;
using AngelinaISAssistant.Live.Ocr;

namespace AngelinaISAssistant.Live.Intel;

/// <summary>
/// 难度数字识别：游戏在屏幕左下角的徽章里显示当前难度（0~18）。
/// 后端会把左下角区域裁剪放大后单独做一次 OCR，这里只负责从识别结果里取出数字。
/// </summary>
internal static class DifficultyReader
{
    public const int MinDifficulty = 0;

    public const int MaxDifficulty = 18;

    public const float MinConfidence = 0.6f;

    /// <summary>从一块区域的 OCR 结果里取出难度数字；取置信度最高的 0~18 数字。</summary>
    public static bool TryParse(IReadOnlyList<OcrLine> lines, out int difficulty)
    {
        difficulty = -1;
        var bestConfidence = 0f;

        foreach (var line in lines)
        {
            if (line.Confidence < MinConfidence)
            {
                continue;
            }

            var text = line.Text.Trim();

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            if (value < MinDifficulty || value > MaxDifficulty)
            {
                continue;
            }

            if (line.Confidence <= bestConfidence)
            {
                continue;
            }

            bestConfidence = line.Confidence;
            difficulty = value;
        }

        return difficulty >= MinDifficulty;
    }
}
