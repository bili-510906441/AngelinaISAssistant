using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("usage: PatchTest <oldDir> <package> <stageDir> <hpatchzSrc>");
            return 2;
        }

        var oldDir = args[0];
        var package = args[1];
        var stage = args[2];
        var hpatchz = args[3];

        // 用独立的模拟安装目录，避免和测试程序自身的文件混在一起
        var baseDir = Path.Combine(AppContext.BaseDirectory, "fake_install");
        if (Directory.Exists(baseDir))
        {
            Directory.Delete(baseDir, true);
        }

        Directory.CreateDirectory(baseDir);
        AngelinaISAssistant.FolderPatch.OverrideBaseDirectory = baseDir;
        foreach (var file in Directory.GetFiles(oldDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(oldDir, file);
            var target = Path.Combine(baseDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }

        var toolDir = Path.Combine(AppContext.BaseDirectory, "tools", "hdiffpatch");
        Directory.CreateDirectory(toolDir);
        File.Copy(hpatchz, Path.Combine(toolDir, "hpatchz.exe"), true);

        if (Directory.Exists(stage))
        {
            Directory.Delete(stage, true);
        }

        Directory.CreateDirectory(stage);

        var result = await AngelinaISAssistant.FolderPatch.ApplyAsync(
            package, stage, message => Console.WriteLine("  " + message));

        Console.WriteLine($"apply status={result.Status} message={result.Message}");

        if (result.Status != AngelinaISAssistant.UpdateApplyStatus.Success)
        {
            return 1;
        }

        var a = File.ReadAllText(Path.Combine(stage, "a.txt"));
        var added = File.Exists(Path.Combine(stage, "added.txt"));
        var changed = File.ReadAllBytes(Path.Combine(stage, "changed.bin"));
        var newChanged = File.ReadAllBytes(Path.Combine(oldDir, "..", "new", "changed.bin"));
        var removeList = File.ReadAllText(Path.Combine(baseDir, "_update_remove.txt"));

        Console.WriteLine($"a.txt={a}");
        Console.WriteLine($"added={added}");
        Console.WriteLine($"changedEqual={changed.SequenceEqual(newChanged)}");
        Console.WriteLine($"remove={removeList.Trim()}");

        var ok = a == "hello v2" && added && changed.SequenceEqual(newChanged) && removeList.Contains("removed.txt");

        // 旧单文件补丁必须被拒绝
        var legacy = Path.Combine(AppContext.BaseDirectory, "legacy.hdiff");
        File.WriteAllText(legacy, "not a real patch");
        var legacyResult = await AngelinaISAssistant.UpdateInstaller.ApplyLocalAsync(legacy, _ => { });
        var legacyMessage = legacyResult.Message ?? string.Empty;
        var legacyRejected = legacyResult.Status == AngelinaISAssistant.UpdateApplyStatus.Failed &&
                             (legacyMessage.Contains(".hdiff", StringComparison.OrdinalIgnoreCase) ||
                              legacyMessage.Contains("LegacyDiffUnsupported", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"legacyRejected={legacyRejected} message={legacyResult.Message}");

        ok = ok && legacyRejected;
        Console.WriteLine(ok ? "PATCH OK" : "PATCH FAIL");
        return ok ? 0 : 1;
    }
}
