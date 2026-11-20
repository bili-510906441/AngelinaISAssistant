PP-OCRv6_small ONNX 模型（实时辅助 OCR 用）
==========================================

来源与转换
----------
- 模型：PaddleOCR / PaddleX 的 PP-OCRv6_small（中英文检测 + 识别）
- 转换工具：paddle2onnx 2.1.0（opset 11，FP32，无 external data）
- 转换后由本项目直接通过 ONNX Runtime 1.32.0（CPU）加载，运行时不依赖 Paddle / Python

文件与 SHA256
-------------
PP-OCRv6_small_det.onnx  9,929,594 B
  56A00E6C881F00924AD09E4E477E05B132574570DA52068C852E66E5065A3FBC

PP-OCRv6_small_rec.onnx 21,159,418 B
  4692BF837097B85A5E4C07F5A5A4200DED4587EC67C509CF6753E4FF33C0F17E

keys.txt 74,945 B / 18,709 行
  23C20FCA6C35470E5F63CAAD69BD69935B89D264B61CF48359FB2ABBAAE21534

接口
----
- det: 输入 x float32 [N,3,H,W]（H/W 为 32 的倍数）→ 输出 fetch_name_0 [N,1,H,W] 概率图
- rec: 输入 x float32 [N,3,48,W] → 输出 fetch_name_0 [N,T,18710]
- keys.txt = 18,708 个字符 + 末尾一个空格行；解码时 class id 0 为 blank，字符取 keys[id-1]
- 该 keys 只适用于 small_rec，不可与 tiny / medium 的字符表混用

许可
----
- 模型来自 PaddleOCR（Apache License 2.0）：https://github.com/PaddlePaddle/PaddleOCR
- 详细声明见发布包根目录的 THIRD-PARTY-NOTICES.md
