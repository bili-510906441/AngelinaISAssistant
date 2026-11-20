using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AngelinaISAssistant.Live.Ocr;

/// <summary>推理使用的 ONNX Runtime 执行后端。</summary>
internal enum OrtExecutionProvider
{
    Cpu,
    Dml
}

/// <summary>
/// ONNX Runtime 1.32 的 C API 绑定（手写，只覆盖 OCR 需要的接口）。
/// 通过 OrtGetApiBase 的版本表访问，不直接 DllImport 具体函数，
/// 这样不会和 App 里已有的 onnxruntime DllImport 解析器互相冲突。
/// </summary>
internal static class OrtNative
{
    private const uint ApiVersion = 32;

    // OrtApi 1.32 中函数指针的顺序（见 onnxruntime_c_api.h）。
    private const int IxGetErrorMessage = 2;
    private const int IxCreateEnv = 3;
    private const int IxCreateSession = 7;
    private const int IxRun = 9;
    private const int IxCreateSessionOptions = 10;
    private const int IxSetSessionLogSeverityLevel = 22;
    private const int IxSetSessionGraphOptimizationLevel = 23;
    private const int IxSetIntraOpNumThreads = 24;
    private const int IxSetInterOpNumThreads = 25;
    private const int IxSessionGetInputCount = 30;
    private const int IxSessionGetOutputCount = 31;
    private const int IxSessionGetInputName = 36;
    private const int IxSessionGetOutputName = 37;
    private const int IxCreateTensorWithDataAsOrtValue = 49;
    private const int IxGetTensorMutableData = 51;
    private const int IxGetTensorElementType = 60;
    private const int IxGetDimensionsCount = 61;
    private const int IxGetDimensions = 62;
    private const int IxGetTensorShapeElementCount = 64;
    private const int IxGetTensorTypeAndShape = 65;
    private const int IxCreateCpuMemoryInfo = 69;
    private const int IxAllocatorFree = 76;
    private const int IxGetAllocatorWithDefaultOptions = 78;
    private const int IxReleaseEnv = 92;
    private const int IxReleaseStatus = 93;
    private const int IxReleaseMemoryInfo = 94;
    private const int IxReleaseSession = 95;
    private const int IxReleaseValue = 96;
    private const int IxReleaseTensorTypeAndShapeInfo = 99;
    private const int IxReleaseSessionOptions = 100;

    private static readonly object Gate = new();
    private static bool _initialized;
    private static bool _available;
    private static string _error = string.Empty;

    private static IntPtr _api;
    private static IntPtr _library;
    private static IntPtr _env;
    private static IntPtr _memoryInfo;
    private static IntPtr _allocator;
    private static uint _apiVersion;
    private static string _nativeDirectory = string.Empty;

    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _available;
        }
    }

    public static string Error
    {
        get
        {
            EnsureInitialized();
            return _error;
        }
    }

    public static string Version { get; private set; } = string.Empty;

    /// <summary>当前加载的 ORT 是否带 DirectML Execution Provider（DirectML 版构建）。</summary>
    public static bool DmlAvailable { get; private set; }

    /// <summary>是否实际加载了 DirectML 版 onnxruntime.dll。</summary>
    public static bool UsesGpuRuntime { get; private set; }

    /// <summary>当前运行时是否为 DirectML 构建。</summary>
    public static bool IsDmlRuntime { get; private set; }

    public static string RuntimeKind => IsDmlRuntime ? "dml" : "cpu";

    /// <summary>当前运行时提供的 EP 列表，便于日志排查。</summary>
    public static string ProviderList { get; private set; } = string.Empty;

    /// <summary>只检查 onnxruntime.dll 是否存在（不加载），供 UI 启动时的状态显示使用。</summary>
    public static bool NativeLibraryPresent
    {
        get
        {
            var baseDirectory = AppContext.BaseDirectory;

            return File.Exists(Path.Combine(baseDirectory, "externals", "onnxruntime.dll"))
                   || File.Exists(Path.Combine(baseDirectory, "externals", "onnxruntime", "onnxruntime.dll"))
                   || File.Exists(Path.Combine(baseDirectory, "onnxruntime", "onnxruntime.dll"))
                   || File.Exists(Path.Combine(baseDirectory, "libs", "onnxruntime", "onnxruntime.dll"))
                   || File.Exists(Path.Combine(baseDirectory, "onnxruntime.dll"));
        }
    }

    /// <summary>DirectML 版 onnxruntime.dll 是否存在（不加载）。</summary>
    public static bool DmlRuntimeFilesPresent
    {
        get
        {
            var baseDirectory = AppContext.BaseDirectory;
            var extra = Environment.GetEnvironmentVariable("AISA_ORT_DML_DIR");

            return File.Exists(Path.Combine(baseDirectory, "externals", "onnxruntime_dml", "onnxruntime.dll"))
                   || File.Exists(Path.Combine(baseDirectory, "onnxruntime_dml", "onnxruntime.dll"))
                   || File.Exists(Path.Combine(baseDirectory, "libs", "onnxruntime_dml", "onnxruntime.dll"))
                   || (!string.IsNullOrWhiteSpace(extra) && File.Exists(Path.Combine(extra, "onnxruntime.dll")));
        }
    }

    public static bool EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return _available;
            }

            _initialized = true;

            try
            {
                if (DmlRuntimeFilesPresent)
                {
                    try
                    {
                        InitializeRuntime(preferDml: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"onnxruntime dml runtime unavailable · {ex.Message}");
                        ResetRuntime();
                        InitializeRuntime(preferDml: false);
                    }
                }
                else
                {
                    InitializeRuntime(preferDml: false);
                }

                _available = true;
                Logger.Info(
                    $"onnxruntime ready · {Version} · runtime={RuntimeKind} · api=v{_apiVersion} · providers={ProviderList}");
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _available = false;
                Logger.Warn($"onnxruntime init failed · {ex.Message}");
            }

            return _available;
        }
    }

    private static void InitializeRuntime(bool preferDml)
    {
        IntPtr library;
        string directory;

        if (preferDml)
        {
            library = LoadDmlNativeLibrary(out directory);
        }
        else
        {
            library = LoadCpuNativeLibrary(out directory);
        }

        _library = library;
        _nativeDirectory = directory;

        var getApiBasePtr = NativeLibrary.GetExport(library, "OrtGetApiBase");
        var getApiBase = Marshal.GetDelegateForFunctionPointer<GetApiBaseDelegate>(getApiBasePtr);
        var apiBase = getApiBase();

        if (apiBase == IntPtr.Zero)
        {
            throw new InvalidOperationException("OrtGetApiBase returned null");
        }

        var getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(Marshal.ReadIntPtr(apiBase));
        _api = IntPtr.Zero;
        _apiVersion = 0;

        var isDml = NativeLibrary.TryGetExport(
            library,
            "OrtSessionOptionsAppendExecutionProvider_DML",
            out _);

        // DirectML 版是 1.24（API v24）；CPU 版是 1.32（API v32）。
        // 先试构建自身的版本，再向下探测兼容其他构建。
        var preferredVersion = isDml ? 24u : 32u;

        _api = getApi(preferredVersion);

        if (_api != IntPtr.Zero)
        {
            _apiVersion = preferredVersion;
        }
        else
        {
            for (var version = 32; version >= 20; version--)
            {
                if (version == preferredVersion)
                {
                    continue;
                }

                _api = getApi((uint)version);

                if (_api != IntPtr.Zero)
                {
                    _apiVersion = (uint)version;
                    break;
                }
            }
        }

        if (_api == IntPtr.Zero)
        {
            throw new InvalidOperationException("OrtApi is not available in this onnxruntime build");
        }

        var getVersionStringPtr = Marshal.ReadIntPtr(apiBase, IntPtr.Size);

        if (getVersionStringPtr != IntPtr.Zero)
        {
            var getVersionString = Marshal.GetDelegateForFunctionPointer<GetVersionStringDelegate>(getVersionStringPtr);
            var versionPtr = getVersionString();
            Version = versionPtr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(versionPtr) ?? string.Empty;
        }

        Check(Call<CreateEnvDelegate>(IxCreateEnv)((int)OrtLoggingLevel.Error, "AngelinaISAssistant", out _env));
        Check(Call<GetAllocatorWithDefaultOptionsDelegate>(IxGetAllocatorWithDefaultOptions)(out _allocator));
        Check(Call<CreateCpuMemoryInfoDelegate>(IxCreateCpuMemoryInfo)(
            (int)OrtAllocatorType.DeviceAllocator,
            (int)OrtMemType.Default,
            out _memoryInfo));

        IsDmlRuntime = isDml;
        DmlAvailable = isDml;
        UsesGpuRuntime = isDml;
        ProviderList = isDml ? "DmlExecutionProvider" : "CPUExecutionProvider";
    }

    private static void ResetRuntime()
    {
        try
        {
            if (_env != IntPtr.Zero)
            {
                Call<ReleaseEnvDelegate>(IxReleaseEnv)(_env);
            }

            if (_memoryInfo != IntPtr.Zero)
            {
                Call<ReleaseMemoryInfoDelegate>(IxReleaseMemoryInfo)(_memoryInfo);
            }
        }
        catch
        {
            // 初始化失败时的清理，忽略
        }

        _api = IntPtr.Zero;
        _library = IntPtr.Zero;
        _apiVersion = 0;
        _env = IntPtr.Zero;
        _memoryInfo = IntPtr.Zero;
        _allocator = IntPtr.Zero;
        _nativeDirectory = string.Empty;
        Version = string.Empty;
        ProviderList = string.Empty;
        DmlAvailable = false;
        IsDmlRuntime = false;
        UsesGpuRuntime = false;
    }

    public static IntPtr CreateSession(
        string modelPath,
        int intraOpThreads,
        OrtExecutionProvider provider = OrtExecutionProvider.Cpu,
        int deviceId = 0)
    {
        EnsureInitialized();

        if (!_available)
        {
            throw new InvalidOperationException("onnxruntime unavailable: " + _error);
        }

        if (provider == OrtExecutionProvider.Dml && !DmlAvailable)
        {
            throw new InvalidOperationException("DmlExecutionProvider is not available");
        }

        IntPtr options = IntPtr.Zero;
        IntPtr session = IntPtr.Zero;

        try
        {
            Check(Call<CreateSessionOptionsDelegate>(IxCreateSessionOptions)(out options));
            Check(Call<SetSessionLogSeverityLevelDelegate>(IxSetSessionLogSeverityLevel)(
                options, (int)OrtLoggingLevel.Error));
            Check(Call<SetSessionGraphOptimizationLevelDelegate>(IxSetSessionGraphOptimizationLevel)(
                options, (int)OrtGraphOptimizationLevel.EnableAll));
            Check(Call<SetIntraOpNumThreadsDelegate>(IxSetIntraOpNumThreads)(options, intraOpThreads));
            Check(Call<SetInterOpNumThreadsDelegate>(IxSetInterOpNumThreads)(options, 1));

            if (provider == OrtExecutionProvider.Dml)
            {
                AppendDmlProvider(options, deviceId);
            }

            Check(Call<CreateSessionDelegate>(IxCreateSession)(_env, modelPath, options, out session));

            return session;
        }
        catch
        {
            if (session != IntPtr.Zero)
            {
                ReleaseSession(session);
            }

            throw;
        }
        finally
        {
            if (options != IntPtr.Zero)
            {
                Call<ReleaseSessionOptionsDelegate>(IxReleaseSessionOptions)(options);
            }
        }
    }

    /// <summary>把 DirectML Execution Provider 追加到 session options（ORT 导出的 C 函数）。</summary>
    private static void AppendDmlProvider(IntPtr sessionOptions, int deviceId)
    {
        var export = NativeLibrary.GetExport(_library, "OrtSessionOptionsAppendExecutionProvider_DML");
        var append = Marshal.GetDelegateForFunctionPointer<AppendDmlProviderDelegate>(export);
        Check(append(sessionOptions, deviceId));
    }

    public static void ReleaseSession(IntPtr session)
    {
        if (session != IntPtr.Zero)
        {
            Call<ReleaseSessionDelegate>(IxReleaseSession)(session);
        }
    }

    public static void ReleaseValue(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            Call<ReleaseValueDelegate>(IxReleaseValue)(value);
        }
    }

    public static string GetInputName(IntPtr session)
    {
        Check(Call<SessionGetInputCountDelegate>(IxSessionGetInputCount)(session, out var count));

        if (count == UIntPtr.Zero)
        {
            throw new InvalidOperationException("model has no inputs");
        }

        Check(Call<SessionGetInputNameDelegate>(IxSessionGetInputName)(
            session, UIntPtr.Zero, _allocator, out var namePtr));

        try
        {
            return Marshal.PtrToStringAnsi(namePtr) ?? string.Empty;
        }
        finally
        {
            if (namePtr != IntPtr.Zero)
            {
                Check(Call<AllocatorFreeDelegate>(IxAllocatorFree)(_allocator, namePtr));
            }
        }
    }

    public static string[] GetOutputNames(IntPtr session)
    {
        var count = GetOutputCount(session);
        var names = new string[count];

        for (var i = 0; i < count; i++)
        {
            Check(Call<SessionGetOutputNameDelegate>(IxSessionGetOutputName)(
                session, (UIntPtr)i, _allocator, out var namePtr));

            try
            {
                names[i] = Marshal.PtrToStringAnsi(namePtr) ?? string.Empty;
            }
            finally
            {
                if (namePtr != IntPtr.Zero)
                {
                    Check(Call<AllocatorFreeDelegate>(IxAllocatorFree)(_allocator, namePtr));
                }
            }
        }

        return names;
    }

    public static int GetOutputCount(IntPtr session)
    {
        Check(Call<SessionGetOutputCountDelegate>(IxSessionGetOutputCount)(session, out var count));
        return checked((int)count.ToUInt64());
    }

    /// <summary>运行单输入、单输出的模型，返回第一路输出的 float 数据和形状。</summary>
    public static float[] Run(IntPtr session, string inputName, string[] outputNames, float[] input, long[] inputShape, out long[] outputShape)
    {
        if (!_available)
        {
            throw new InvalidOperationException("onnxruntime unavailable: " + _error);
        }

        var inputNamePtr = Marshal.StringToHGlobalAnsi(inputName);
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        IntPtr inputValue = IntPtr.Zero;
        IntPtr[]? outputs = null;
        IntPtr[]? outputNamePtrs = null;

        try
        {
            Check(Call<CreateTensorWithDataAsOrtValueDelegate>(IxCreateTensorWithDataAsOrtValue)(
                _memoryInfo,
                inputHandle.AddrOfPinnedObject(),
                (UIntPtr)(input.Length * sizeof(float)),
                inputShape,
                (UIntPtr)inputShape.Length,
                (int)OnnxTensorElementDataType.Float,
                out inputValue));

            outputs = new IntPtr[outputNames.Length];
            outputNamePtrs = new IntPtr[outputNames.Length];

            for (var i = 0; i < outputNames.Length; i++)
            {
                outputNamePtrs[i] = Marshal.StringToHGlobalAnsi(outputNames[i]);
            }

            Check(Call<RunDelegate>(IxRun)(
                session,
                IntPtr.Zero,
                new[] { inputNamePtr },
                new[] { inputValue },
                (UIntPtr)1,
                outputNamePtrs,
                (UIntPtr)outputNamePtrs.Length,
                outputs));

            if (outputs.Length == 0 || outputs[0] == IntPtr.Zero)
            {
                throw new InvalidOperationException("onnxruntime returned no output");
            }

            Check(Call<GetTensorTypeAndShapeDelegate>(IxGetTensorTypeAndShape)(outputs[0], out var shapeInfo));

            try
            {
                Check(Call<GetTensorElementTypeDelegate>(IxGetTensorElementType)(shapeInfo, out var elementType));

                if (elementType != (int)OnnxTensorElementDataType.Float)
                {
                    throw new InvalidOperationException($"unexpected output tensor type: {elementType}");
                }

                Check(Call<GetDimensionsCountDelegate>(IxGetDimensionsCount)(shapeInfo, out var rank));
                var rankValue = checked((int)rank.ToUInt64());
                outputShape = new long[rankValue];

                if (rankValue > 0)
                {
                    Check(Call<GetDimensionsDelegate>(IxGetDimensions)(shapeInfo, outputShape, rank));
                }

                Check(Call<GetTensorShapeElementCountDelegate>(IxGetTensorShapeElementCount)(
                    shapeInfo, out var elementCount));
                var count = checked((int)elementCount.ToUInt64());

                Check(Call<GetTensorMutableDataDelegate>(IxGetTensorMutableData)(outputs[0], out var dataPtr));

                var result = new float[count];
                if (count > 0)
                {
                    Marshal.Copy(dataPtr, result, 0, count);
                }

                return result;
            }
            finally
            {
                Call<ReleaseTensorTypeAndShapeInfoDelegate>(IxReleaseTensorTypeAndShapeInfo)(shapeInfo);
            }
        }
        finally
        {
            if (outputs is not null)
            {
                foreach (var output in outputs)
                {
                    ReleaseValue(output);
                }
            }

            ReleaseValue(inputValue);

            if (inputHandle.IsAllocated)
            {
                inputHandle.Free();
            }

            if (inputNamePtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputNamePtr);
            }

            if (outputNamePtrs is not null)
            {
                foreach (var pointer in outputNamePtrs)
                {
                    if (pointer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(pointer);
                    }
                }
            }
        }
    }

    private static T Call<T>(int index) where T : Delegate
    {
        if (_api == IntPtr.Zero)
        {
            throw new InvalidOperationException("onnxruntime is not initialized");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(_api, index * IntPtr.Size));
    }

    private static void Check(IntPtr status)
    {
        if (status == IntPtr.Zero)
        {
            return;
        }

        var message = "onnxruntime error";

        try
        {
            var messagePtr = Call<GetErrorMessageDelegate>(IxGetErrorMessage)(status);

            if (messagePtr != IntPtr.Zero)
            {
                message = Marshal.PtrToStringAnsi(messagePtr) ?? message;
            }
        }
        finally
        {
            Call<ReleaseStatusDelegate>(IxReleaseStatus)(status);
        }

        throw new InvalidOperationException(message);
    }

    private static IntPtr LoadDmlNativeLibrary(out string directory)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var extra = Environment.GetEnvironmentVariable("AISA_ORT_DML_DIR");
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(extra))
        {
            candidates.Add(Path.Combine(extra, "onnxruntime.dll"));
        }

        candidates.Add(Path.Combine(baseDirectory, "externals", "onnxruntime_dml", "onnxruntime.dll"));
        candidates.Add(Path.Combine(baseDirectory, "onnxruntime_dml", "onnxruntime.dll"));
        candidates.Add(Path.Combine(baseDirectory, "libs", "onnxruntime_dml", "onnxruntime.dll"));

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var candidateDirectory = Path.GetDirectoryName(candidate) ?? baseDirectory;

            // DirectML.dll 和 onnxruntime.dll 放在同一目录，需要让 loader 能找到
            try
            {
                SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs | LoadLibrarySearchUserDirs);
                AddDllDirectory(candidateDirectory);
            }
            catch
            {
                // 搜索路径设置失败仍尝试加载
            }

            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                directory = candidateDirectory;
                return handle;
            }
        }

        throw new FileNotFoundException("DirectML onnxruntime.dll not found", string.Join("; ", candidates));
    }

    private static IntPtr LoadCpuNativeLibrary(out string directory)
    {
        var baseDirectory = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(baseDirectory, "externals", "onnxruntime.dll"),
            Path.Combine(baseDirectory, "externals", "onnxruntime", "onnxruntime.dll"),
            Path.Combine(baseDirectory, "onnxruntime", "onnxruntime.dll"),
            Path.Combine(baseDirectory, "libs", "onnxruntime", "onnxruntime.dll"),
            Path.Combine(baseDirectory, "onnxruntime.dll")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                directory = Path.GetDirectoryName(candidate) ?? baseDirectory;
                return handle;
            }
        }

        // 兜底：让系统按默认规则找一次
        if (NativeLibrary.TryLoad("onnxruntime", typeof(OrtNative).Assembly, null, out var fallback))
        {
            directory = baseDirectory;
            return fallback;
        }

        throw new FileNotFoundException("onnxruntime.dll not found", string.Join("; ", candidates));
    }

    private enum OrtLoggingLevel
    {
        Verbose = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Fatal = 4
    }

    private enum OrtGraphOptimizationLevel
    {
        DisableAll = 0,
        EnableBasic = 1,
        EnableExtended = 2,
        EnableAll = 99
    }

    private enum OrtAllocatorType
    {
        DeviceAllocator = 0,
        ArenaAllocator = 1
    }

    private enum OrtMemType
    {
        Default = 0
    }

    private enum OnnxTensorElementDataType
    {
        Float = 1
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetApiBaseDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetApiDelegate(uint version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetVersionStringDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetErrorMessageDelegate(IntPtr status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ReleaseStatusDelegate(IntPtr status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateEnvDelegate(int severity, [MarshalAs(UnmanagedType.LPStr)] string logId, out IntPtr env);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ReleaseEnvDelegate(IntPtr env);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateSessionOptionsDelegate(out IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ReleaseSessionOptionsDelegate(IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SetSessionLogSeverityLevelDelegate(IntPtr options, int level);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SetSessionGraphOptimizationLevelDelegate(IntPtr options, int level);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SetIntraOpNumThreadsDelegate(IntPtr options, int threads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SetInterOpNumThreadsDelegate(IntPtr options, int threads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateSessionDelegate(
        IntPtr env,
        [MarshalAs(UnmanagedType.LPWStr)] string modelPath,
        IntPtr options,
        out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ReleaseSessionDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SessionGetInputCountDelegate(IntPtr session, out UIntPtr count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SessionGetOutputCountDelegate(IntPtr session, out UIntPtr count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SessionGetInputNameDelegate(
        IntPtr session,
        UIntPtr index,
        IntPtr allocator,
        out IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SessionGetOutputNameDelegate(
        IntPtr session,
        UIntPtr index,
        IntPtr allocator,
        out IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetAllocatorWithDefaultOptionsDelegate(out IntPtr allocator);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AllocatorFreeDelegate(IntPtr allocator, IntPtr pointer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateCpuMemoryInfoDelegate(int allocatorType, int memType, out IntPtr memoryInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ReleaseMemoryInfoDelegate(IntPtr memoryInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateTensorWithDataAsOrtValueDelegate(
        IntPtr memoryInfo,
        IntPtr data,
        UIntPtr dataLength,
        long[] shape,
        UIntPtr shapeLength,
        int elementType,
        out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ReleaseValueDelegate(IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr RunDelegate(
        IntPtr session,
        IntPtr runOptions,
        IntPtr[] inputNames,
        IntPtr[] inputs,
        UIntPtr inputCount,
        IntPtr[]? outputNames,
        UIntPtr outputNameCount,
        [Out] IntPtr[] outputs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetTensorTypeAndShapeDelegate(IntPtr value, out IntPtr shapeInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ReleaseTensorTypeAndShapeInfoDelegate(IntPtr shapeInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetTensorElementTypeDelegate(IntPtr shapeInfo, out int elementType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetDimensionsCountDelegate(IntPtr shapeInfo, out UIntPtr count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetDimensionsDelegate(IntPtr shapeInfo, [Out] long[] dimensions, UIntPtr count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetTensorShapeElementCountDelegate(IntPtr shapeInfo, out UIntPtr count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetTensorMutableDataDelegate(IntPtr value, out IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AppendDmlProviderDelegate(IntPtr sessionOptions, int deviceId);

    private const uint LoadLibrarySearchUserDirs = 0x00000400;
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
