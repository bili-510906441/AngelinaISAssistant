using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AngelinaISAssistant;

/// <summary>
/// 同一目录只允许运行一个实例。互斥体按程序目录哈希命名，不同目录的副本互不影响。
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;

    /// <summary>尝试占位；返回 false 表示同一目录已有实例在运行。</summary>
    public static bool TryAcquire()
    {
        try
        {
            var directory = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToLowerInvariant())))[..16];

            var mutex = new Mutex(initiallyOwned: true, $"Local\\AngelinaISAssistant.{hash}", out var createdNew);

            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            _mutex = mutex;
            return true;
        }
        catch
        {
            // 互斥体不可用时不要阻止程序启动
            return true;
        }
    }

    public static void Release()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // 未持有或已被释放
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
