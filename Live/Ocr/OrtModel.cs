using System;

namespace AngelinaISAssistant.Live.Ocr;

/// <summary>一个已加载的 ONNX 模型会话（单输入、单输出）。</summary>
internal sealed class OrtModel : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _session;
    private readonly string _inputName;
    private readonly string[] _outputNames;

    public OrtModel(
        string modelPath,
        int intraOpThreads,
        OrtExecutionProvider provider = OrtExecutionProvider.Cpu,
        int deviceId = 0)
    {
        ModelPath = modelPath;
        Provider = provider;
        _session = OrtNative.CreateSession(modelPath, intraOpThreads, provider, deviceId);
        _inputName = OrtNative.GetInputName(_session);
        _outputNames = OrtNative.GetOutputNames(_session);
    }

    public string ModelPath { get; }

    public OrtExecutionProvider Provider { get; }

    public string InputName => _inputName;

    public float[] Run(float[] input, long[] inputShape, out long[] outputShape)
    {
        lock (_gate)
        {
            if (_session == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(OrtModel));
            }

            return OrtNative.Run(_session, _inputName, _outputNames, input, inputShape, out outputShape);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            OrtNative.ReleaseSession(_session);
            _session = IntPtr.Zero;
        }
    }
}
