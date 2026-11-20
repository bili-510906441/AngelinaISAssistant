using System;

namespace AngelinaISAssistant.Live.Ocr;

/// <summary>按相对区域裁剪一帧画面并放大，用于「左下角难度徽章」这类固定角落的小元素识别。</summary>
internal static class OcrRegion
{
    /// <summary>
    /// 从 BGRA 整帧里裁出相对区域（0~1）并放大，返回可直接交给 <see cref="PpOcrEngine.Recognize"/> 的 BGRA 图。
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) CropScaled(
        byte[] bgra,
        int width,
        int height,
        double x0,
        double y0,
        double x1,
        double y1,
        double scale)
    {
        var left = Math.Clamp((int)(width * x0), 0, width - 1);
        var top = Math.Clamp((int)(height * y0), 0, height - 1);
        var right = Math.Clamp((int)(width * x1), left + 1, width);
        var bottom = Math.Clamp((int)(height * y1), top + 1, height);

        var cropWidth = right - left;
        var cropHeight = bottom - top;
        var crop = new byte[cropWidth * cropHeight * 4];

        for (var row = 0; row < cropHeight; row++)
        {
            Buffer.BlockCopy(
                bgra,
                ((top + row) * width + left) * 4,
                crop,
                row * cropWidth * 4,
                cropWidth * 4);
        }

        var scaledWidth = Math.Max(1, (int)Math.Round(cropWidth * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(cropHeight * scale));
        var bgr = OcrImage.BgraToBgr(crop, cropWidth, cropHeight);
        var scaledBgr = OcrImage.ResizeBilinear(bgr, cropWidth, cropHeight, scaledWidth, scaledHeight);

        var scaledBgra = new byte[scaledWidth * scaledHeight * 4];

        for (var i = 0; i < scaledWidth * scaledHeight; i++)
        {
            scaledBgra[i * 4] = scaledBgr[i * 3];
            scaledBgra[i * 4 + 1] = scaledBgr[i * 3 + 1];
            scaledBgra[i * 4 + 2] = scaledBgr[i * 3 + 2];
            scaledBgra[i * 4 + 3] = 255;
        }

        return (scaledBgra, scaledWidth, scaledHeight);
    }
}
