using System;
using System.Text;

namespace AngelinaISAssistant.Live.Intel;

/// <summary>
/// OCR 文本归一化：只保留汉字 / 假名 / 字母 / 数字，丢弃空白、标点和符号。
/// 关卡名、敌人名都按归一化结果做精确匹配，因此「所守者，义」与「所守者义」、
/// 「ISW-NO 猎犬病原」与「猎犬病原」、英文下划线/空格写法都能对上。
/// </summary>
internal static class IsText
{
    /// <summary>关卡代码前缀（归一化后形如 iswno），匹配前先去掉，避免「ISW-NO 关卡名」整行对不上。</summary>
    private static readonly string[] StageCodePrefixes = { "iswno", "iswdf", "iswsp", "iswdu" };

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            if (ch is >= '\u4e00' and <= '\u9fff')      // CJK 汉字
            {
                builder.Append(ch);
            }
            else if (ch is >= '\u3040' and <= '\u30ff') // 平假名 / 片假名
            {
                builder.Append(ch);
            }
            else if (ch is >= '\uac00' and <= '\ud7af') // 谚文
            {
                builder.Append(ch);
            }
            else if (ch is >= '0' and <= '9')
            {
                builder.Append(ch);
            }
            else if (ch is >= 'a' and <= 'z')
            {
                builder.Append(ch);
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                builder.Append((char)(ch + 32));
            }
            else if (ch is >= '\uff10' and <= '\uff19') // 全角数字
            {
                builder.Append((char)('0' + (ch - '\uff10')));
            }
            else if (ch is >= '\uff21' and <= '\uff3a') // 全角大写字母
            {
                builder.Append((char)('a' + (ch - '\uff21')));
            }
            else if (ch is >= '\uff41' and <= '\uff5a') // 全角小写字母
            {
                builder.Append((char)('a' + (ch - '\uff41')));
            }

            // 其它字符（空格、标点、·、引号、括号等）全部丢弃
        }

        var normalized = builder.ToString();

        foreach (var prefix in StageCodePrefixes)
        {
            if (normalized.Length > prefix.Length && normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                normalized = normalized[prefix.Length..];
                break;
            }
        }

        return normalized;
    }

    /// <summary>判断归一化后的 OCR 行是否包含某个标签（标签会被归一化）。</summary>
    public static bool ContainsLabel(string? text, string label)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var normalizedLine = Normalize(text);
        var normalizedLabel = Normalize(label);

        return normalizedLabel.Length > 0 && normalizedLine.Contains(normalizedLabel, StringComparison.Ordinal);
    }
}
