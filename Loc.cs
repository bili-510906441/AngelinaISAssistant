using System;
using System.Linq;
using System.Windows;

namespace AngelinaISAssistant;

/// <summary>界面文案的本地化：切换时替换 Application 里的字符串字典，XAML 的 DynamicResource 会自动刷新。</summary>
public static class Loc
{
    public const string Chinese = "zh-CN";
    public const string ChineseTraditional = "zh-TW";
    public const string English = "en-US";
    public const string Japanese = "ja-JP";

    public static string Current { get; private set; } = Chinese;

    public static event EventHandler? LanguageChanged;

    public static void Apply(string language, bool force = false)
    {
        if (!force && string.Equals(language, Current, StringComparison.Ordinal))
        {
            return;
        }

        Current = language;

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(
            dictionary => dictionary.Source?.OriginalString?.Contains("Strings.", StringComparison.Ordinal) == true);

        if (existing is not null)
        {
            merged.Remove(existing);
        }

        try
        {
            merged.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/{typeof(Loc).Assembly.GetName().Name};component/Resources/Strings.{language}.xaml", UriKind.Absolute)
            });
        }
        catch
        {
            // 文案字典缺失时回退到英文，保证界面不会因为语言设置而无法启动
            merged.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/{typeof(Loc).Assembly.GetName().Name};component/Resources/Strings.{English}.xaml", UriKind.Absolute)
            });
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>把系统/配置里的语言标识归一到本项目支持的四套文案。</summary>
    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return Chinese;
        }

        var value = language.Trim().Replace('_', '-');

        if (value.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            var traditional =
                value.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("-TW", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("-HK", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("-MO", StringComparison.OrdinalIgnoreCase);

            return traditional ? ChineseTraditional : Chinese;
        }

        if (value.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return Japanese;
        }

        if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return English;
        }

        return Chinese;
    }

    public static string T(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object[] args) => string.Format(T(key), args);
}
