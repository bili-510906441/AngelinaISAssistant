using System;
using System.Collections.Generic;
using System.IO;

namespace AngelinaISAssistant;

/// <summary>阻止程序被直接放在盘符根目录、临时目录、系统/共享目录等高危位置。</summary>
internal static class StartupLocationGuard
{
    /// <summary>返回命中的位置类别；位置正常时返回 null。</summary>
    public static string? Check() => CheckDirectory(AppContext.BaseDirectory);

    internal static string? CheckDirectory(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (full.Length == 0)
        {
            return null;
        }

        // 盘符根目录，例如 D:\
        if (string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return "drive-root";
        }

        foreach (var (name, path) in RestrictedDirectories())
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var restricted = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (restricted.Length == 0)
            {
                continue;
            }

            if (string.Equals(full, restricted, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(restricted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    private static IEnumerable<(string Name, string? Path)> RestrictedDirectories()
    {
        yield return ("temp", Path.GetTempPath());
        yield return ("windows", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        yield return ("system", Environment.SystemDirectory);
        yield return ("program-files", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        yield return ("program-files", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        yield return ("program-data", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        var publicRoot = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        if (!string.IsNullOrEmpty(publicRoot))
        {
            yield return ("public", Path.GetDirectoryName(publicRoot));
        }
    }
}
