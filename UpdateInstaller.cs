using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// 从 GitHub Release 拉取更新包并应用：
/// 优先用 angelina_isa_v旧_v新.hdiff 增量包打补丁，没有则整包 angelina_isa_v新_full.zip。
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

        if (assets.Count == 0)
        {
            return new UpdateApplyResult(UpdateApplyStatus.NotFound, Loc.T("Str.Update.NetworkError"));
        }

        var hdiffName = $"angelina_isa_v{local}_v{remote}.hdiff";
        var zipName = $"angelina_isa_v{remote}_full.zip";

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

        var hdiff = assets.FirstOrDefault(a => string.Equals(a.Name, hdiffName, StringComparison.OrdinalIgnoreCase));

        if (hdiff is not null)
        {
            var result = await ApplyDiffAsync(hdiff, stage, progress, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }
        else
        {
            var zip = assets.FirstOrDefault(a => string.Equals(a.Name, zipName, StringComparison.OrdinalIgnoreCase));
            if (zip is null)
            {
                return new UpdateApplyResult(UpdateApplyStatus.NotFound, Loc.T("Str.Update.NetworkError"));
            }

            var result = await ApplyFullPackageAsync(zip, stage, progress, cancellationToken);
            if (result is not null)
            {
                return result;
            }
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

    private static async Task<UpdateApplyResult?> ApplyDiffAsync(
        ReleaseAsset asset,
        string stage,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        progress(Loc.T("Str.Update.Downloading"));

        var patchPath = Path.Combine(Path.GetTempPath(), "angelina_update.hdiff");

        try
        {
            await DownloadAsync(asset.DownloadUrl, patchPath, cancellationToken);
        }
        catch
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, Loc.T("Str.Update.NetworkError"));
        }

        progress(Loc.T("Str.Update.Applying"));

        try
        {
            var current = Path.Combine(AppContext.BaseDirectory, AppFileName);
            var patched = Path.Combine(stage, AppFileName);

            var exitCode = await RunToolAsync(
                IncrementalUpdate.HpatchzPath,
                $"-f \"{current}\" \"{patchPath}\" \"{patched}\"");

            if (exitCode != 0 || !File.Exists(patched) || new FileInfo(patched).Length == 0)
            {
                return new UpdateApplyResult(UpdateApplyStatus.Failed, Loc.T("Str.Update.Failed"));
            }
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, ex.Message);
        }
        finally
        {
            TryDelete(patchPath);
        }

        return null;
    }

    private static async Task<UpdateApplyResult?> ApplyFullPackageAsync(
        ReleaseAsset asset,
        string stage,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        progress(Loc.T("Str.Update.Downloading"));

        var zipPath = Path.Combine(Path.GetTempPath(), "angelina_update.zip");

        try
        {
            await DownloadAsync(asset.DownloadUrl, zipPath, cancellationToken);
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

        return null;
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

    private static async Task DownloadAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AngelinaISAssistant");

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(path);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static async Task<int> RunToolAsync(string exe, string arguments)
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
