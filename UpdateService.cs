using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AngelinaISAssistant;

internal enum UpdateCheckState
{
    Unknown,
    Checking,
    UpToDate,
    Outdated,
    Failed
}

internal sealed record UpdateCheckResult(UpdateCheckState State, string? RemoteVersion = null);

/// <summary>
/// 用 B 站空间公告的第一行做版本检测。目前没有实际更新包，只判断版本号。
/// </summary>
internal static class UpdateService
{
    private const string NoticeEndpoint = "https://api.bilibili.com/x/space/notice?mid=510906441";
    private const string SpaceUrl = "https://space.bilibili.com/510906441";

    private static readonly Regex VersionPattern = new(@"v?(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);

    public static async Task<UpdateCheckResult> CheckAsync(string localVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri(SpaceUrl);

            var json = await client.GetStringAsync(NoticeEndpoint, cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || code.GetInt32() != 0)
            {
                return new UpdateCheckResult(UpdateCheckState.Failed);
            }

            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            {
                return new UpdateCheckResult(UpdateCheckState.Failed);
            }

            var firstLine = FirstLine(data.GetString());
            if (firstLine is null)
            {
                return new UpdateCheckResult(UpdateCheckState.Failed);
            }

            var remoteText = VersionPattern.Match(firstLine);
            if (!remoteText.Success ||
                !Version.TryParse(remoteText.Groups[1].Value, out var remoteVersion) ||
                !Version.TryParse(VersionPattern.Match(localVersion).Groups[1].Value, out var local))
            {
                return new UpdateCheckResult(UpdateCheckState.Failed);
            }

            var state = remoteVersion > local ? UpdateCheckState.Outdated : UpdateCheckState.UpToDate;
            return new UpdateCheckResult(state, remoteVersion.ToString());
        }
        catch
        {
            // 网络异常、超时、风控、JSON 结构变化都按失败处理
            return new UpdateCheckResult(UpdateCheckState.Failed);
        }
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim().Trim('\r');
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }
}
