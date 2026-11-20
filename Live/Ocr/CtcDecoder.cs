using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AngelinaISAssistant.Live.Ocr;

/// <summary>small_rec 的字符表：18709 行 = 18708 个字符 + 末尾一个空格。不可与其他尺寸的模型混用。</summary>
internal sealed class OcrKeys
{
    public const int ExpectedCount = 18709;

    private readonly string[] _items;

    private OcrKeys(string[] items)
    {
        _items = items;
    }

    public IReadOnlyList<string> Items => _items;

    public int Count => _items.Length;

    public static OcrKeys Load(string path)
    {
        var text = File.ReadAllText(path);

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // 文件末尾的换行会产生一个空元素；字符表本身可能有「空格」行，不要 Trim
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            Array.Resize(ref lines, lines.Length - 1);
        }

        if (lines.Length != ExpectedCount)
        {
            throw new InvalidDataException(
                $"keys.txt has {lines.Length} lines, expected {ExpectedCount} (PP-OCRv6_small_rec)");
        }

        return new OcrKeys(lines);
    }
}

/// <summary>CTC 贪心解码：blank=0，字符 id = argmax - 1，相邻重复只保留一个。</summary>
internal static class CtcDecoder
{
    public static string Decode(
        float[] probabilities,
        int timeSteps,
        int classes,
        IReadOnlyList<string> keys,
        out float confidence)
    {
        var builder = new StringBuilder();
        var previous = -1;
        double sum = 0;

        for (var t = 0; t < timeSteps; t++)
        {
            var offset = t * classes;
            var best = 0;
            var bestValue = float.MinValue;

            for (var c = 0; c < classes; c++)
            {
                var value = probabilities[offset + c];

                if (value > bestValue)
                {
                    bestValue = value;
                    best = c;
                }
            }

            sum += bestValue;

            if (best != 0 && best != previous)
            {
                var keyIndex = best - 1;

                if (keyIndex >= 0 && keyIndex < keys.Count)
                {
                    builder.Append(keys[keyIndex]);
                }
            }

            previous = best;
        }

        confidence = timeSteps == 0 ? 0 : (float)(sum / timeSteps);
        return builder.ToString();
    }
}
