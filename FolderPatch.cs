using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AngelinaISAssistant;

/// <summary>
/// 文件夹级增量包（.aisapatch）：由 hdiffz 的目录模式生成，hpatchz 直接读旧安装目录、
/// 写出完整的新目录，不需要额外的清单文件，删除/新增/重命名都由原生格式处理。
/// </summary>
internal static class FolderPatch
{
    public const string RemoveListName = "_update_remove.txt";

    /// <summary>安装目录里属于运行期数据、不参与比对的目录。</summary>
    private static readonly string[] ProtectedDirectories =
    {
        "_update_stage",
        "_update_pkg",
        "config",
        "reports",
        "logs"
    };

    /// <summary>仅用于测试：替换安装目录。</summary>
    internal static string? OverrideBaseDirectory { get; set; }

    public static bool LooksLikePackage(string path) =>
        path.EndsWith(".aisapatch", StringComparison.OrdinalIgnoreCase);

    public static async Task<UpdateApplyResult> ApplyAsync(
        string packagePath,
        string stage,
        Action<string> progress,
        CancellationToken cancellationToken = default)
    {
        var baseDir = OverrideBaseDirectory ?? AppContext.BaseDirectory;

        try
        {
            // hpatchz 的目录模式要求输出目录不存在
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, true);
            }
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, ex.Message);
        }

        progress(Loc.T("Str.Update.ApplyingFolder"));

        int exitCode;

        try
        {
            exitCode = await UpdateInstaller.RunToolAsync(
                IncrementalUpdate.HpatchzPath,
                $"-s-64m \"{baseDir.TrimEnd(Path.DirectorySeparatorChar)}\" \"{packagePath}\" \"{stage}\"");
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, ex.Message);
        }

        if (exitCode != 0 || !Directory.Exists(stage))
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, Loc.T("Str.Update.FolderPatchFailed"));
        }

        try
        {
            var removals = CollectRemovals(baseDir, stage);
            File.WriteAllLines(Path.Combine(baseDir, RemoveListName), removals);
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, ex.Message);
        }

        return new UpdateApplyResult(UpdateApplyStatus.Success);
    }

    /// <summary>旧安装目录里存在、但新目录里已经没有的文件，交给更新脚本删除。</summary>
    private static List<string> CollectRemovals(string baseDir, string stage)
    {
        var result = new List<string>();

        foreach (var file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(baseDir, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (segments.Length > 1 && Array.IndexOf(ProtectedDirectories, segments[0]) >= 0)
            {
                continue;
            }

            if (segments[0] is "_update.cmd" or RemoveListName || segments[0].EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(stage, relative)))
            {
                result.Add(relative.Replace('/', '\\'));
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
