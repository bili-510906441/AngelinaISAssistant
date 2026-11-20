using System;
using System.Globalization;

namespace AngelinaISAssistant.Live.Intel;

/// <summary>
/// 敌人数值的显示格式（设置里可选）：
/// 万（35.8w）/ 科学计数法（3.58e5）/ 完整数字（358000）。
/// </summary>
internal static class NumberDisplay
{
    /// <summary>阈值：达到 1 万才用「万」或科学计数法，小数值直接显示，便于阅读。</summary>
    private const double CompactThreshold = 10000;

    public static string Format(double value, NumberDisplayFormat format) => format switch
    {
        NumberDisplayFormat.Scientific => FormatScientific(value),
        NumberDisplayFormat.Plain => FormatPlain(value),
        _ => FormatWan(value)
    };

    private static string FormatPlain(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 1e-9)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }

        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string FormatWan(double value)
    {
        if (Math.Abs(value) < CompactThreshold)
        {
            return FormatPlain(value);
        }

        return (value / 10000).ToString("0.0", CultureInfo.InvariantCulture) + "w";
    }

    private static string FormatScientific(double value)
    {
        if (Math.Abs(value) < CompactThreshold)
        {
            return FormatPlain(value);
        }

        var exponent = (int)Math.Floor(Math.Log10(Math.Abs(value)));
        var mantissa = value / Math.Pow(10, exponent);

        if (Math.Abs(mantissa) >= 10)
        {
            mantissa /= 10;
            exponent++;
        }

        return mantissa.ToString("0.##", CultureInfo.InvariantCulture)
               + "e"
               + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
