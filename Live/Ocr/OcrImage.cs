using System;

namespace AngelinaISAssistant.Live.Ocr;

internal readonly record struct OcrPoint(float X, float Y);

/// <summary>OCR 用到的像素变换：BGRA→BGR、缩放、归一化、旋转矩形裁剪。</summary>
internal static class OcrImage
{
    public const int RecHeight = 48;

    private const float MeanR = 0.485f;
    private const float MeanG = 0.456f;
    private const float MeanB = 0.406f;
    private const float StdR = 0.229f;
    private const float StdG = 0.224f;
    private const float StdB = 0.225f;

    public static byte[] BgraToBgr(byte[] bgra, int width, int height)
    {
        var pixels = checked(width * height);

        if (bgra.Length < pixels * 4)
        {
            throw new ArgumentException("frame buffer is smaller than width * height * 4", nameof(bgra));
        }

        var bgr = new byte[pixels * 3];

        for (var i = 0; i < pixels; i++)
        {
            var s = i * 4;
            var d = i * 3;
            bgr[d] = bgra[s];
            bgr[d + 1] = bgra[s + 1];
            bgr[d + 2] = bgra[s + 2];
        }

        return bgr;
    }

    public static byte[] ResizeBilinear(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        if (sourceWidth == width && sourceHeight == height)
        {
            return source;
        }

        var result = new byte[checked(width * height * 3)];
        var scaleX = (double)sourceWidth / width;
        var scaleY = (double)sourceHeight / height;

        for (var y = 0; y < height; y++)
        {
            var sy = (y + 0.5) * scaleY - 0.5;
            var y0 = (int)Math.Floor(sy);
            var wy = sy - y0;

            if (y0 < 0)
            {
                y0 = 0;
                wy = 0;
            }

            var y1 = Math.Min(y0 + 1, sourceHeight - 1);

            for (var x = 0; x < width; x++)
            {
                var sx = (x + 0.5) * scaleX - 0.5;
                var x0 = (int)Math.Floor(sx);
                var wx = sx - x0;

                if (x0 < 0)
                {
                    x0 = 0;
                    wx = 0;
                }

                var x1 = Math.Min(x0 + 1, sourceWidth - 1);

                var i00 = (y0 * sourceWidth + x0) * 3;
                var i10 = (y0 * sourceWidth + x1) * 3;
                var i01 = (y1 * sourceWidth + x0) * 3;
                var i11 = (y1 * sourceWidth + x1) * 3;

                var o = (y * width + x) * 3;

                for (var c = 0; c < 3; c++)
                {
                    var top = source[i00 + c] * (1 - wx) + source[i10 + c] * wx;
                    var bottom = source[i01 + c] * (1 - wx) + source[i11 + c] * wx;
                    var value = top * (1 - wy) + bottom * wy;
                    result[o + c] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
            }
        }

        return result;
    }

    /// <summary>det 输入：RGB 顺序、/255、按 ImageNet 均值方差归一化、CHW。</summary>
    public static void FillDetInput(byte[] bgr, int width, int height, float[] destination)
    {
        var pixels = checked(width * height);

        if (destination.Length < pixels * 3)
        {
            throw new ArgumentException("destination too small", nameof(destination));
        }

        var channel = pixels;

        for (var i = 0; i < pixels; i++)
        {
            var s = i * 3;
            var r = bgr[s + 2] / 255f;
            var g = bgr[s + 1] / 255f;
            var b = bgr[s] / 255f;

            destination[i] = (r - MeanR) / StdR;
            destination[channel + i] = (g - MeanG) / StdG;
            destination[channel * 2 + i] = (b - MeanB) / StdB;
        }
    }

    /// <summary>rec 输入：等比缩放到高 48，右侧补 0，BGR、/255、±0.5 归一化、CHW。</summary>
    public static float[] BuildRecInput(byte[] crop, int width, int height, out int inputWidth)
    {
        var aspect = (double)width / height;
        var rawWidth = RecHeight * Math.Max(320.0 / RecHeight, aspect);
        inputWidth = Math.Clamp((int)rawWidth, 1, 3200);

        var resizedWidth = Math.Clamp((int)Math.Ceiling(RecHeight * aspect), 1, inputWidth);
        var resized = ResizeBilinear(crop, width, height, resizedWidth, RecHeight);

        var channel = RecHeight * inputWidth;
        var input = new float[channel * 3];

        for (var y = 0; y < RecHeight; y++)
        {
            for (var x = 0; x < resizedWidth; x++)
            {
                var s = (y * resizedWidth + x) * 3;
                var index = y * inputWidth + x;

                input[index] = resized[s + 2] / 255f - 0.5f;
                input[index] /= 0.5f;
                input[channel + index] = (resized[s + 1] / 255f - 0.5f) / 0.5f;
                input[channel * 2 + index] = (resized[s] / 255f - 0.5f) / 0.5f;
            }
        }

        return input;
    }

    /// <summary>按旋转矩形从原图裁剪并拉正；竖长文本会逆时针旋转 90°（与 PaddleOCR 一致）。</summary>
    public static byte[] CropRotatedRect(
        byte[] bgr,
        int sourceWidth,
        int sourceHeight,
        OcrPoint topLeft,
        OcrPoint topRight,
        OcrPoint bottomRight,
        OcrPoint bottomLeft,
        out int cropWidth,
        out int cropHeight)
    {
        var width = Math.Max(1, (int)Math.Round(Math.Max(Distance(topLeft, topRight), Distance(bottomLeft, bottomRight))));
        var height = Math.Max(1, (int)Math.Round(Math.Max(Distance(topLeft, bottomLeft), Distance(topRight, bottomRight))));

        var crop = new byte[checked(width * height * 3)];

        for (var y = 0; y < height; y++)
        {
            var v = (y + 0.5) / height;

            for (var x = 0; x < width; x++)
            {
                var u = (x + 0.5) / width;

                var sourceX = topLeft.X + u * (topRight.X - topLeft.X) + v * (bottomLeft.X - topLeft.X);
                var sourceY = topLeft.Y + u * (topRight.Y - topLeft.Y) + v * (bottomLeft.Y - topLeft.Y);

                SampleBilinear(bgr, sourceWidth, sourceHeight, (float)sourceX, (float)sourceY, crop, (y * width + x) * 3);
            }
        }

        if ((double)height / width >= 1.5)
        {
            // 逆时针旋转 90°：new[y, x] = old[x, width-1-y]
            var rotated = new byte[checked(width * height * 3)];

            for (var y = 0; y < width; y++)
            {
                for (var x = 0; x < height; x++)
                {
                    var sourceIndex = (x * width + (width - 1 - y)) * 3;
                    var targetIndex = (y * height + x) * 3;
                    rotated[targetIndex] = crop[sourceIndex];
                    rotated[targetIndex + 1] = crop[sourceIndex + 1];
                    rotated[targetIndex + 2] = crop[sourceIndex + 2];
                }
            }

            crop = rotated;
            (width, height) = (height, width);
        }

        cropWidth = width;
        cropHeight = height;
        return crop;
    }

    public static float Distance(OcrPoint a, OcrPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static void SampleBilinear(
        byte[] source,
        int width,
        int height,
        float x,
        float y,
        byte[] destination,
        int destinationIndex)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var wx = x - x0;
        var wy = y - y0;

        var x1 = Math.Clamp(x0 + 1, 0, width - 1);
        var y1 = Math.Clamp(y0 + 1, 0, height - 1);
        x0 = Math.Clamp(x0, 0, width - 1);
        y0 = Math.Clamp(y0, 0, height - 1);

        var i00 = (y0 * width + x0) * 3;
        var i10 = (y0 * width + x1) * 3;
        var i01 = (y1 * width + x0) * 3;
        var i11 = (y1 * width + x1) * 3;

        for (var c = 0; c < 3; c++)
        {
            var top = source[i00 + c] * (1 - wx) + source[i10 + c] * wx;
            var bottom = source[i01 + c] * (1 - wx) + source[i11 + c] * wx;
            destination[destinationIndex + c] = (byte)Math.Clamp((int)Math.Round(top * (1 - wy) + bottom * wy), 0, 255);
        }
    }
}
