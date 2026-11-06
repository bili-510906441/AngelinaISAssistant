using System;
using System.Linq;
using System.Windows;

namespace AngelinaISAssistant;

/// <summary>界面文案的本地化：切换时替换 Application 里的字符串字典，XAML 的 DynamicResource 会自动刷新。</summary>
public static class Loc
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

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

        merged.Add(new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{language}.xaml", UriKind.Relative)
        });

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object[] args) => string.Format(T(key), args);
}
