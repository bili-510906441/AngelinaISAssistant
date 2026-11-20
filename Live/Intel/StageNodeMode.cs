using System;
using System.Collections.Generic;
using AngelinaISAssistant.Live.Ocr;

namespace AngelinaISAssistant.Live.Intel;

/// <summary>
/// 地图节点类型识别：关卡名上方会显示「作战」或「紧急作战」。
/// 该类型决定用 n_mods 还是 elite_mods，以及是否应用紧急作战的敌人替换。
/// </summary>
internal static class StageNodeMode
{
    public const string Combat = "作战";

    public const string Emergency = "紧急作战";

    private const float MinConfidence = 0.6f;

    /// <summary>
    /// 判断当前关卡节点是不是「紧急作战」：true=紧急作战，false=作战，null=没识别到。
    /// 关卡名上方的标签和关卡名同一栏，按相对位置动态查找（不写死坐标）。
    /// </summary>
    public static bool? Detect(IReadOnlyList<OcrLine> lines, int frameWidth, int frameHeight, string? stageDisplay)
    {
        if (string.IsNullOrEmpty(stageDisplay) || lines.Count == 0)
        {
            return null;
        }

        var stageKey = IsText.Normalize(stageDisplay);
        OcrLine? stageLine = null;

        foreach (var line in lines)
        {
            if (line.Confidence < MinConfidence)
            {
                continue;
            }

            if (!string.Equals(IsText.Normalize(line.Text), stageKey, StringComparison.Ordinal))
            {
                continue;
            }

            // 关卡名在右侧节点面板里
            if (line.X + line.Width / 2 < frameWidth * 0.45)
            {
                continue;
            }

            if (stageLine is null || line.Confidence > stageLine.Confidence)
            {
                stageLine = line;
            }
        }

        if (stageLine is null)
        {
            return null;
        }

        var stageCenterX = stageLine.X + stageLine.Width / 2;
        var maxHorizontalDistance = Math.Max(stageLine.Width, 200);
        var maxVerticalDistance = Math.Max(stageLine.Height * 4, 160);

        bool? detected = null;
        var bestDistance = double.MaxValue;

        foreach (var line in lines)
        {
            if (line.Confidence < MinConfidence)
            {
                continue;
            }

            // 只找关卡名正上方的标签
            if (line.Y + line.Height > stageLine.Y + 8 || line.Y < stageLine.Y - maxVerticalDistance)
            {
                continue;
            }

            if (line.Y < frameHeight * 0.02)
            {
                continue;
            }

            var normalized = IsText.Normalize(line.Text);
            bool isEmergency;

            if (string.Equals(normalized, IsText.Normalize(Emergency), StringComparison.Ordinal))
            {
                isEmergency = true;
            }
            else if (string.Equals(normalized, IsText.Normalize(Combat), StringComparison.Ordinal))
            {
                isEmergency = false;
            }
            else
            {
                continue;
            }

            var centerX = line.X + line.Width / 2;

            if (Math.Abs(centerX - stageCenterX) > maxHorizontalDistance)
            {
                continue;
            }

            // 取离关卡名最近的那一条（同一栏里可能还有地图节点上的「作战」文字）
            var distance = stageLine.Y - (line.Y + line.Height);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                detected = isEmergency;
            }
        }

        return detected;
    }
}
