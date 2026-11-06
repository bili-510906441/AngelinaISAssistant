using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AngelinaISAssistant;

/// <summary>黑流树海（ro6）的一个关卡。</summary>
internal sealed class StageEntry
{
    private static readonly Regex MarkupPattern = new("<[^>]*>");

    private ImageSource? _image;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("boss")]
    public bool IsBoss { get; set; }

    [JsonPropertyName("elite")]
    public bool IsElite { get; set; }

    [JsonPropertyName("dlc")]
    public int Dlc { get; set; }

    [JsonPropertyName("types")]
    public int[] Types { get; set; } = Array.Empty<int>();

    [JsonPropertyName("layers")]
    public int[] Layers { get; set; } = Array.Empty<int>();

    public bool IsDlc1 => Dlc == 1;

    /// <summary>只有打开关卡详情时才会解码图片。</summary>
    public ImageSource? Image => _image ??= LoadImage();

    public string TypeText => string.Join(" / ", Types.Where(t => t > 0).Select(TypeLabel));

    public string LayerText => string.Join(" / ", Layers.Select(LayerLabel));

    public string SummaryText => string.Join(" · ", new[] { TypeText, LayerText }.Where(s => s.Length > 0));

    public string DlcText => $"DLC{Dlc}";

    public string DisplayDescription => MarkupPattern
        .Replace(Description.Replace("\\n", "\n"), string.Empty)
        .Trim();

    private static string TypeLabel(int type) => type switch
    {
        2 => Loc.T("Str.Type.Normal"),
        3 => Loc.T("Str.Type.Ending"),
        4 => Loc.T("Str.Type.Pursuit"),
        5 => Loc.T("Str.Type.Event"),
        _ => string.Empty
    };

    private static string LayerLabel(int layer) =>
        layer <= 0 ? Loc.T("Str.Layer.Any") : Loc.F("Str.Layer.N", layer);

    private ImageSource? LoadImage()
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri($"pack://application:,,,/Resources/Maps/ro6/{Id}.png");
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>关卡清单，首次访问时从嵌入资源里读取。</summary>
internal static class StageCatalog
{
    private static List<StageEntry>? _stages;

    public static IReadOnlyList<StageEntry> Ro6 => _stages ??= Load();

    private static List<StageEntry> Load()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/ro6_stages.json"));
            if (resource is null)
            {
                return new List<StageEntry>();
            }

            using var stream = resource.Stream;
            return JsonSerializer.Deserialize<List<StageEntry>>(stream) ?? new List<StageEntry>();
        }
        catch
        {
            return new List<StageEntry>();
        }
    }
}
