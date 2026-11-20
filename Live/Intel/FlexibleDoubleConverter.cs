using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AngelinaISAssistant.Live.Intel;

/// <summary>
/// 兼容 JSON 里数值修正的 value 可能是字符串的情况：
/// 例如系统修正 level_enemy_replace 的 value 是敌人 key（"enemy_1024_mortar_2"），
/// 这类修正不参与四则运算，按 0 处理即可；数字字符串正常解析。
/// </summary>
internal sealed class FlexibleDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetDouble();

            case JsonTokenType.String:
            {
                var text = reader.GetString();

                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : 0;
            }

            case JsonTokenType.Null:
                return 0;

            default:
                reader.Skip();
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
