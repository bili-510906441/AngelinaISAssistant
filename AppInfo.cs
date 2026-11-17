using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SmokeTest")]
[assembly: InternalsVisibleTo("PatchTest")]

namespace AngelinaISAssistant;

/// <summary>全局版本信息。</summary>
internal static class AppInfo
{
    public const string Version = "v0.0.3";
}
