using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AngelinaISAssistant;

internal enum UpdateApplyStatus
{
    Success,
    NotFound,
    Failed
}

internal sealed record UpdateApplyResult(UpdateApplyStatus Status, string? Message = null);

internal sealed record ReleaseAsset(string Name, string DownloadUrl);

/// <summary>
/// 从 GitHub Release 拉取更新包并应用。
/// 优先顺序由设置决定：文件夹级增量包 angelina_isa_v旧_v新.aisapatch 或整包 angelina_isa_v新_full.zip。
/// 增量包针对主程序 AngelinaISAssistant.dll。
/// </summary>
internal static class UpdateInstaller
{
    private const string Repo = "bili-510906441/AngelinaISAssistant";
    private const string AppFileName = "AngelinaISAssistant.dll";
    private const string StageFolder = "_update_stage";
    private const string ScriptName = "_update.cmd";

    public static async Task<UpdateApplyResult> RunAsync(
        string remoteVersion,
        UpdatePackagePreference preference,
        Action<string> progress,
        CancellationToken cancellationToken = default)
    {
        var local = Normalize(AppInfo.Version);
        var remote = Normalize(remoteVersion);

        if (local.Length == 0 || remote.Length == 0)
        {
            return new UpdateApplyResult(UpdateApplyStatus.NotFound, Loc.T("Str.Update.NetworkError"));
        }

        List<ReleaseAsset> assets;
        try
        {
            assets = await FetchAssetsAsync(remote, cancellationToken);
        }
        catch
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, Loc.T("Str.Update.NetworkError"));
        }

        var folderName = $"angelina_isa_v{local}_v{remote}.aisapatch";
        var zipName = $"angelina_isa_v{remote}_full.zip";

        var folderAsset = assets.FirstOrDefault(a => string.Equals(a.Name, folderName, StringComparison.OrdinalIgnoreCase));
        var zipAsset = assets.FirstOrDefault(a => string.Equals(a.Name, zipName, StringComparison.OrdinalIgnoreCase));

        var candidates = new List<(ReleaseAsset Asset, PackageKind Kind)>();

        if (preference == UpdatePackagePreference.Full)
        {
            if (zipAsset is not null) candidates.Add((zipAsset, PackageKind.Full));
            if (folderAsset is not null) candidates.Add((folderAsset, PackageKind.Folder));
        }
        else
        {
            if (folderAsset is not null) candidates.Add((folderAsset, PackageKind.Folder));
            if (zipAsset is not null) candidates.Add((zipAsset, PackageKind.Full));
        }

        if (candidates.Count == 0)
        {
            return new UpdateApplyResult(UpdateApplyStatus.NotFound, Loc.T("Str.Update.NetworkError"));
        }

        var stage = Path.Combine(AppContext.BaseDirectory, StageFolder);

        UpdateApplyResult? lastError = null;

        foreach (var (asset, kind) in candidates)
        {
            try
            {
                if (Directory.Exists(stage))
                {
                    Directory.Delete(stage, true);
                }

                Directory.CreateDirectory(stage);
            }
            catch (Exception ex)
            {
                return new UpdateApplyResult(UpdateApplyStatus.Failed, ex.Message);
            }

            var result = kind == PackageKind.Folder
                ? await ApplyFolderPackageAsync(asset, stage, progress, cancellationToken)
                : await ApplyFullPackageAsync(asset, stage, progress, cancellationToken);

            if (result.Status == UpdateApplyStatus.Success)
            {
                lastError = null;
                break;
            }

            lastError = result;
        }

        if (lastError is not null)
        {
            return lastError;
        }

        progress(Loc.T("Str.Update.Restart"));

        try
        {
            WriteUpdateScript();
            LaunchUpdateScript();
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, ex.Message);
        }

        return new UpdateApplyResult(UpdateApplyStatus.Success);
    }

    private static async Task<UpdateApplyResult> ApplyFolderPackageAsync(
        ReleaseAsset asset,
        string stage,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        var packagePath = Path.Combine(Path.GetTempPath(), "angelina_update.aisapatch");

        try
        {
            await DownloadAsync(
                asset.DownloadUrl,
                packagePath,
                "Str.Update.DownloadingDiff",
                asset.Name,
                progress,
                cancellationToken);
        }
        catch
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, Loc.T("Str.Update.NetworkError"));
        }

        try
        {
            return await FolderPatch.ApplyAsync(packagePath, stage, progress, cancellationToken);
        }
        finally
        {
            TryDelete(packagePath);
        }
    }

    private enum PackageKind
    {
        Folder,
        Full
    }


    /// <summary>应用用户拖入本地的更新包（.aisapatch 文件夹级增量 或 _full.zip 全量；.hdiff 已不再支持）。</summary>
    public static async Task<UpdateApplyResult> ApplyLocalAsync(
        string packagePath,
        Action<string> progress,
        CancellationToken cancellationToken = default)
    {
        // 旧的单文件补丁（仅覆盖 dll）已不再支持
        if (packagePath.EndsWith(".hdiff", StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, Loc.T("Str.Update.LegacyDiffUnsupported"));
        }

        var stage = Path.Combine(AppContext.BaseDirectory, StageFolder);

        try
        {
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, true);
            }

            Directory.CreateDirectory(stage);
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, ex.Message);
        }

        progress(Loc.T("Str.Update.Applying"));

        UpdateApplyResult result;

        if (FolderPatch.LooksLikePackage(packagePath))
        {
            result = await FolderPatch.ApplyAsync(packagePath, stage, progress, cancellationToken);
        }
        else
        {
            try
            {
                ZipFile.ExtractToDirectory(packagePath, stage, true);
                FlattenStage(stage);
                result = new UpdateApplyResult(UpdateApplyStatus.Success);
            }
            catch (Exception ex)
            {
                result = new UpdateApplyResult(UpdateApplyStatus.Failed, ex.Message);
            }
        }

        if (result.Status != UpdateApplyStatus.Success)
        {
            return result;
        }

        try
        {
            WriteUpdateScript();
            LaunchUpdateScript();
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, ex.Message);
        }

        return new UpdateApplyResult(UpdateApplyStatus.Success);
    }

    private static async Task<UpdateApplyResult> ApplyFullPackageAsync(
        ReleaseAsset asset,
        string stage,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "angelina_update.zip");

        try
        {
            await DownloadAsync(
                asset.DownloadUrl,
                zipPath,
                "Str.Update.DownloadingFull",
                asset.Name,
                progress,
                cancellationToken);
        }
        catch
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, Loc.T("Str.Update.NetworkError"));
        }

        progress(Loc.T("Str.Update.Applying"));

        try
        {
            ZipFile.ExtractToDirectory(zipPath, stage, true);
            FlattenStage(stage);
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, ex.Message);
        }
        finally
        {
            TryDelete(zipPath);
        }

        return new UpdateApplyResult(UpdateApplyStatus.Success);
    }

    /// <summary>如果压缩包只有一层根目录，把内容提上来，方便整体覆盖。</summary>
    private static void FlattenStage(string stage)
    {
        var files = Directory.GetFiles(stage);
        var directories = Directory.GetDirectories(stage);

        if (files.Length != 0 || directories.Length != 1)
        {
            return;
        }

        var inner = directories[0];

        foreach (var file in Directory.GetFiles(inner))
        {
            File.Move(file, Path.Combine(stage, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(inner))
        {
            Directory.Move(directory, Path.Combine(stage, Path.GetFileName(directory)));
        }

        Directory.Delete(inner);
    }

    private static async Task<List<ReleaseAsset>> FetchAssetsAsync(string remoteVersion, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AngelinaISAssistant");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var url = $"https://api.github.com/repos/{Repo}/releases/tags/v{remoteVersion}";
        using var response = await client.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new List<ReleaseAsset>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var assets = new List<ReleaseAsset>();

        if (document.RootElement.TryGetProperty("assets", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var download = item.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;

                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(download))
                {
                    assets.Add(new ReleaseAsset(name, download));
                }
            }
        }

        return assets;
    }

    /// <summary>下载并持续回报进度：文件名 + 百分比 + 已下载/总大小。</summary>
    private static async Task DownloadAsync(
        string url,
        string path,
        string kindKey,
        string displayName,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AngelinaISAssistant");

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;

        progress(Describe(kindKey, displayName, 0, 0, total));

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(path);

        var buffer = new byte[81920];
        long received = 0;
        var lastPercent = -1;

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;

            var percent = total > 0 ? (int)(received * 100 / total) : 0;

            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress(Describe(kindKey, displayName, percent, received, total));
            }
        }

        progress(Describe(kindKey, displayName, 100, received, total > 0 ? total : received));
    }

    private static string Describe(string kindKey, string name, int percent, long received, long total)
    {
        var header = Loc.F(kindKey, name);

        var body = total > 0
            ? Loc.F("Str.Update.Progress", percent, ToMb(received), ToMb(total))
            : Loc.F("Str.Update.DownloadedOnly", ToMb(received));

        return header + Environment.NewLine + body;
    }

    private static string ToMb(long bytes) =>
        (bytes / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture);

    internal static async Task<int> RunToolAsync(string exe, string arguments)
    {
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"找不到 hpatchz：{exe}", exe);
        }

        var startInfo = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static string Normalize(string version)
    {
        var value = (version ?? string.Empty).Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            value = value[1..];
        }

        return value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件删不掉也无所谓
        }
    }

    private static void WriteUpdateScript()
    {
        var content = string.Join(
            "\r\n",
            "@echo off",
            "cd /d \"%~dp0\"",
            ":wait",
            "tasklist /fi \"imagename eq AngelinaISAssistant.exe\" 2>nul | find /i \"AngelinaISAssistant.exe\" >nul",
            "if not errorlevel 1 (",
            "    ping -n 2 127.0.0.1 >nul",
            "    goto wait",
            ")",
            "xcopy \"_update_stage\\*\" \".\\\" /E /Y /I /Q >nul",
            "rd /s /q \"_update_stage\" >nul 2>&1",
            "rd /s /q \"_update_pkg\" >nul 2>&1",
            "if exist \"_update_remove.txt\" (",
            "    for /f \"usebackq delims=\" %%L in (\"_update_remove.txt\") do if not \"%%L\"==\"\" del /q \"%%L\" >nul 2>&1",
            "    del /q \"_update_remove.txt\" >nul 2>&1",
            ")",
            "start \"\" \"AngelinaISAssistant.exe\"",
            "(goto) 2>nul & del \"%~f0\"",
            string.Empty);

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, ScriptName), content, Encoding.ASCII);
    }

    private static void LaunchUpdateScript()
    {
        var script = Path.Combine(AppContext.BaseDirectory, ScriptName);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        });
    }
}
