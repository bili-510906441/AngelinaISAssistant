using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AngelinaISAssistant;

/// <summary>
/// hdiffpatch 增量更新封装。
/// 客户端只带 hpatchz，用它把逐文件补丁应用到旧文件上（补丁由 version_tools 里的 make_update.ps1 生成）。
/// </summary>
internal static class IncrementalUpdate
{

    public static string ToolsDirectory => Path.Combine(AppContext.BaseDirectory, "tools", "hdiffpatch");


    public static string HpatchzPath => Path.Combine(ToolsDirectory, "hpatchz.exe");

    /// <summary>客户端只需要 hpatchz。</summary>
    public static bool IsAvailable => File.Exists(HpatchzPath);


    /// <summary>客户端：把补丁应用到旧版本文件，输出新版本文件。</summary>
    public static async Task ApplyPatchAsync(string oldFile, string patchFile, string newFile, CancellationToken cancellationToken = default)
    {
        EnsureTool(HpatchzPath);

        var exitCode = await RunAsync(
            HpatchzPath,
            $"-f \"{oldFile}\" \"{patchFile}\" \"{newFile}\"",
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"hpatchz 失败，退出码 {exitCode}。");
        }
    }

    private static void EnsureTool(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到 hdiffpatch 工具：{path}", path);
        }
    }

    private static async Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = ToolsDirectory
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return process.ExitCode;
    }
}
