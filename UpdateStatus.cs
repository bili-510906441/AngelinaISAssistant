namespace AngelinaISAssistant;

/// <summary>
/// 版本状态。默认「不是最新」为 false，等更新检查返回结果后再更新；
/// 检查失败时界面会单独显示「更新失败」，不会误报为最新。
/// </summary>
internal static class UpdateStatus
{
    public static bool IsOutdated { get; private set; }

    public static void SetOutdated(bool outdated)
    {
        IsOutdated = outdated;
    }
}
