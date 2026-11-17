using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AngelinaISAssistant;

/// <summary>
/// ONNX Runtime 1.32.0（win-x64，CPU）的原生入口。
/// 只做“能不能用、是什么版本”的探测；具体推理请在此之上封装 Session。
/// </summary>
internal static class OnnxRuntimeNative
{
    private const string LibraryName = "onnxruntime";

    private static bool _probed;
    private static bool _available;
    private static string _version = string.Empty;

    public static bool IsAvailable
    {
        get
        {
            Probe();
            return _available;
        }
    }

    public static string Version
    {
        get
        {
            Probe();
            return _version;
        }
    }

    public static string NativeDirectory => Path.Combine(AppContext.BaseDirectory, "externals", "onnxruntime");

    private static void Probe()
    {
        if (_probed)
        {
            return;
        }

        _probed = true;

        try
        {
            var apiBase = OrtGetApiBase();

            if (apiBase == IntPtr.Zero)
            {
                return;
            }

            // OrtApiBase 前两个字段：GetApi 与 GetVersionString 的函数指针
            var getVersionString = Marshal.ReadIntPtr(apiBase, IntPtr.Size);
            var version = Marshal.GetDelegateForFunctionPointer<GetVersionStringDelegate>(getVersionString)();

            _version = Marshal.PtrToStringAnsi(version) ?? string.Empty;
            _available = true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"onnxruntime unavailable · {ex.Message}");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetVersionStringDelegate();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr OrtGetApiBase();
}
