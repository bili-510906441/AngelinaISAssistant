using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AngelinaISAssistant;

/// <summary>
/// hdiffpatch 增量更新封装。
/// 发布端用 hdiffz 生成增量补丁，客户端用 hpatchz 把补丁应用到旧文件上。
/// 目前还没有实际 diff 文件，这里只是把调用链准备好，尚未接到更新流程里。
/// </summary>
internal static class IncrementalUpdate
{
    /// <summary>
    /// 体积最小的 diff 参数（README 的 exe 基准里 6.45%，是表中最小的一组）：
    /// -m-6            逐字节精细匹配（matchScore=6），补丁最小，代价是内存和耗时
    /// -SD             单压缩流格式，支持边下载边 patch、多线程解压
    /// -c-lzma2-9-16m  LZMA2 级别 9、16MB 字典
    /// -p-8            8 线程压缩，只影响速度，不影响体积
    /// </summary>
    public const string DiffArguments = "-m-6 -SD -d -f -p-8 -c-lzma2-9-16m";

    public static string ToolsDirectory => Path.Combine(AppContext.BaseDirectory, "tools", "hdiffpatch");

    public static string HdiffzPath => Path.Combine(ToolsDirectory, "hdiffz.exe");

    public static string HpatchzPath => Path.Combine(ToolsDirectory, "hpatchz.exe");

    public static bool IsAvailable => File.Exists(HpatchzPath) && File.Exists(HdiffzPath);

    /// <summary>发布端：由旧版本与新版本生成增量补丁，返回补丁路径。</summary>
    public static async Task<string> CreatePatchAsync(string oldFile, string newFile, string patchFile, CancellationToken cancellationToken = default)
    {
        EnsureTool(HdiffzPath);

        var exitCode = await RunAsync(
            HdiffzPath,
            $"{DiffArguments} \"{oldFile}\" \"{newFile}\" \"{patchFile}\"",
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"hdiffz 失败，退出码 {exitCode}。");
        }

        return patchFile;
    }

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
