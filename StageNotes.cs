using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AngelinaISAssistant;

/// <summary>关卡笔记（错题本），按关卡 id 独立保存。</summary>
internal static class StageNotes
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AngelinaISAssistant",
        "notes.json");

    private static Dictionary<string, string>? _notes;

    public static string Get(string stageId)
    {
        var notes = Load();
        return notes.TryGetValue(stageId, out var text) ? text : string.Empty;
    }

    public static void Set(string stageId, string text)
    {
        var notes = Load();

        if (string.IsNullOrWhiteSpace(text))
        {
            notes.Remove(stageId);
        }
        else
        {
            notes[stageId] = text;
        }

        Save(notes);
    }

    private static Dictionary<string, string> Load()
    {
        if (_notes is not null)
        {
            return _notes;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                _notes = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            }
        }
        catch
        {
            // 读不出来就从空笔记开始
        }

        return _notes ??= new Dictionary<string, string>();
    }

    private static void Save(Dictionary<string, string> notes)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(notes));
        }
        catch
        {
            // 保存失败不影响使用
        }
    }
}
