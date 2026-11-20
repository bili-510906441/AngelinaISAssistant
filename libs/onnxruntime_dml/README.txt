DirectML 版 ONNX Runtime（随发布包分发，约 36 MB）
====================================================

本目录是 GPU 推理的 DirectML 运行时，不需要 CUDA / cuDNN：

  onnxruntime.dll                    17,328,152   (ONNX Runtime 1.24.4 DirectML 构建)
  onnxruntime_providers_shared.dll       22,040
  DirectML.dll                       18,527,776   (Microsoft.AI.DirectML 1.15.4 x64)
  ORT-LICENSE.txt
  DirectML-LICENSE.txt
  DirectML-ThirdPartyNotices.txt

下载来源
--------
1) ONNX Runtime DirectML 1.24.4：
   https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/1.24.4/microsoft.ml.onnxruntime.directml.1.24.4.nupkg
   取 runtimes/win-x64/native/onnxruntime.dll 与 onnxruntime_providers_shared.dll

2) Microsoft.AI.DirectML 1.15.4：
   https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.4/microsoft.ai.directml.1.15.4.nupkg
   只取 bin/x64-win/DirectML.dll（整包约 200 MB，其余是其它架构/Xbox 版本）

放置位置
--------
csproj 会把它复制到：
  bin/.../externals/onnxruntime_dml/
  release/externals/onnxruntime_dml/

程序加载顺序：
  externals\onnxruntime_dml\onnxruntime.dll
  onnxruntime_dml\onnxruntime.dll
  libs\onnxruntime_dml\onnxruntime.dll
也可用环境变量 AISA_ORT_DML_DIR 指向该目录。

注意
----
- ORT API 版本 v24；OrtNative 会先请求 v24，CPU 版（v32）会先请求 v32。
- DirectML 版 ORT 也包含 CPU EP，DML 失败会自动回退 CPU。
- 首次推理有 DML 着色器编译开销，程序启动后会在后台预热。
- 详细说明：Live\Ocr\GPU_DIRECTML_GUIDE.md
