using System;
using System.Collections.Generic;

namespace AngelinaISAssistant.Live.Ocr;

/// <summary>det 得到的一条文本区域（旋转矩形，坐标是 det 输入图的像素坐标）。</summary>
internal readonly record struct DetBox(
    OcrPoint TopLeft,
    OcrPoint TopRight,
    OcrPoint BottomRight,
    OcrPoint BottomLeft,
    float Width,
    float Height,
    float Score);

/// <summary>
/// PP-OCRv6_small_det 的 DB 后处理。
/// 纯 C# 实现：二值化 → 连通域边界 → 最小外接矩形 → 框内均值过滤 → 外扩（unclip）。
/// 参数取自模型 inference.yml：thresh=0.2，box_thresh=0.45，unclip_ratio=1.4，max_candidates=3000。
/// </summary>
internal static class DbPostProcess
{
    public const float Thresh = 0.2f;
    public const float BoxThresh = 0.45f;
    public const float UnclipRatio = 1.4f;
    public const int MaxCandidates = 3000;

    private const float MinInitialSide = 3f;
    private const float MinUnclippedSide = 5f;

    public static List<DetBox> Run(float[] probability, int width, int height)
    {
        var components = FindComponents(probability, width, height);

        if (components.Count == 0)
        {
            return new List<DetBox>();
        }

        // 面积大的优先（等同于限制候选数量，避免噪声区域占用名额）
        components.Sort((a, b) => b.Area.CompareTo(a.Area));

        var boxes = new List<DetBox>(Math.Min(components.Count, 128));
        var limit = Math.Min(components.Count, MaxCandidates);

        for (var i = 0; i < limit; i++)
        {
            var component = components[i];
            var rect = MinAreaRect(component.Boundary);

            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            if (Math.Min(rect.Width, rect.Height) < MinInitialSide)
            {
                continue;
            }

            var score = MeanProbability(probability, width, height, rect);

            if (score < BoxThresh)
            {
                continue;
            }

            var distance = rect.Width * rect.Height * UnclipRatio / (2f * (rect.Width + rect.Height));
            var expanded = Expand(rect, distance);

            if (Math.Min(expanded.Width, expanded.Height) < MinUnclippedSide)
            {
                continue;
            }

            boxes.Add(new DetBox(
                expanded.TopLeft,
                expanded.TopRight,
                expanded.BottomRight,
                expanded.BottomLeft,
                expanded.Width,
                expanded.Height,
                score));
        }

        boxes.Sort((a, b) =>
        {
            var ay = Math.Min(Math.Min(a.TopLeft.Y, a.TopRight.Y), Math.Min(a.BottomLeft.Y, a.BottomRight.Y));
            var by = Math.Min(Math.Min(b.TopLeft.Y, b.TopRight.Y), Math.Min(b.BottomLeft.Y, b.BottomRight.Y));
            var compare = ay.CompareTo(by);
            return compare != 0
                ? compare
                : Math.Min(a.TopLeft.X, a.BottomLeft.X).CompareTo(Math.Min(b.TopLeft.X, b.BottomLeft.X));
        });

        return boxes;
    }

    private sealed class Component
    {
        public int Area;
        public List<OcrPoint> Boundary = new();
    }

    private static List<Component> FindComponents(float[] probability, int width, int height)
    {
        var visited = new bool[width * height];
        var components = new List<Component>();
        var stack = new Stack<int>();

        for (var start = 0; start < visited.Length; start++)
        {
            if (visited[start] || probability[start] <= Thresh)
            {
                continue;
            }

            var component = new Component();
            visited[start] = true;
            stack.Push(start);

            while (stack.Count > 0)
            {
                var index = stack.Pop();
                component.Area++;

                var x = index % width;
                var y = index / width;

                if (IsBoundary(probability, visited, width, height, x, y))
                {
                    component.Boundary.Add(new OcrPoint(x, y));
                }

                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        var nx = x + dx;
                        var ny = y + dy;

                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        var neighbour = ny * width + nx;

                        if (visited[neighbour] || probability[neighbour] <= Thresh)
                        {
                            continue;
                        }

                        visited[neighbour] = true;
                        stack.Push(neighbour);
                    }
                }
            }

            if (component.Area >= 3 && component.Boundary.Count > 0)
            {
                components.Add(component);
            }
        }

        return components;
    }

    private static bool IsBoundary(float[] probability, bool[] visited, int width, int height, int x, int y)
    {
        // 只把 4 邻域作为边界判断：避免 8 邻域连接造成的锯齿/对角误判
        if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
        {
            return true;
        }

        var index = y * width + x;

        return probability[index - 1] <= Thresh
               || probability[index + 1] <= Thresh
               || probability[index - width] <= Thresh
               || probability[index + width] <= Thresh;
    }

    private static (OcrPoint TopLeft, OcrPoint TopRight, OcrPoint BottomRight, OcrPoint BottomLeft, float Width, float Height)
        MinAreaRect(List<OcrPoint> points)
    {
        if (points.Count == 0)
        {
            return (default, default, default, default, 0, 0);
        }

        var hull = ConvexHull(points);

        if (hull.Count < 3)
        {
            return (default, default, default, default, 0, 0);
        }

        var bestArea = float.MaxValue;
        var bestCorners = new OcrPoint[4];

        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);

            if (length < 1e-4f)
            {
                continue;
            }

            var ux = dx / length;
            var uy = dy / length;

            float minU = float.MaxValue, maxU = float.MinValue;
            float minV = float.MaxValue, maxV = float.MinValue;

            foreach (var point in hull)
            {
                var u = point.X * ux + point.Y * uy;
                var v = -point.X * uy + point.Y * ux;

                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            var rectWidth = maxU - minU;
            var rectHeight = maxV - minV;
            var area = rectWidth * rectHeight;

            if (area >= bestArea)
            {
                continue;
            }

            bestArea = area;

            bestCorners[0] = new OcrPoint(ux * minU + -uy * minV, uy * minU + ux * minV);
            bestCorners[1] = new OcrPoint(ux * maxU + -uy * minV, uy * maxU + ux * minV);
            bestCorners[2] = new OcrPoint(ux * maxU + -uy * maxV, uy * maxU + ux * maxV);
            bestCorners[3] = new OcrPoint(ux * minU + -uy * maxV, uy * minU + ux * maxV);
        }

        var ordered = OrderPoints(bestCorners);
        var width = Distance(ordered[0], ordered[1]);
        var height = Distance(ordered[0], ordered[3]);
        return (ordered[0], ordered[1], ordered[2], ordered[3], width, height);
    }

    private static (OcrPoint TopLeft, OcrPoint TopRight, OcrPoint BottomRight, OcrPoint BottomLeft, float Width, float Height)
        Expand((OcrPoint TopLeft, OcrPoint TopRight, OcrPoint BottomRight, OcrPoint BottomLeft, float Width, float Height) rect,
            float distance)
    {
        var ex = rect.TopRight.X - rect.TopLeft.X;
        var ey = rect.TopRight.Y - rect.TopLeft.Y;
        var length = MathF.Sqrt(ex * ex + ey * ey);

        if (length < 1e-4f)
        {
            return rect;
        }

        var ux = ex / length;
        var uy = ey / length;

        var fx = rect.BottomLeft.X - rect.TopLeft.X;
        var fy = rect.BottomLeft.Y - rect.TopLeft.Y;
        var length2 = MathF.Sqrt(fx * fx + fy * fy);

        if (length2 < 1e-4f)
        {
            return rect;
        }

        var vx = fx / length2;
        var vy = fy / length2;

        OcrPoint Shift(OcrPoint origin, float du, float dv) => new(
            origin.X + (ux * du + vx * dv) * distance,
            origin.Y + (uy * du + vy * dv) * distance);

        return (
            Shift(rect.TopLeft, -1, -1),
            Shift(rect.TopRight, 1, -1),
            Shift(rect.BottomRight, 1, 1),
            Shift(rect.BottomLeft, -1, 1),
            rect.Width + distance * 2,
            rect.Height + distance * 2);
    }

    private static float Distance(OcrPoint a, OcrPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static OcrPoint[] OrderPoints(OcrPoint[] points)
    {
        var topLeft = points[0];
        var bottomRight = points[0];
        var topRight = points[0];
        var bottomLeft = points[0];

        foreach (var point in points)
        {
            if (point.X + point.Y < topLeft.X + topLeft.Y) topLeft = point;
            if (point.X + point.Y > bottomRight.X + bottomRight.Y) bottomRight = point;
            if (point.X - point.Y > topRight.X - topRight.Y) topRight = point;
            if (point.X - point.Y < bottomLeft.X - bottomLeft.Y) bottomLeft = point;
        }

        return new[] { topLeft, topRight, bottomRight, bottomLeft };
    }

    private static List<OcrPoint> ConvexHull(List<OcrPoint> points)
    {
        var sorted = new List<OcrPoint>(points);
        sorted.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        var hull = new List<OcrPoint>(sorted.Count * 2);

        foreach (var point in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(point);
        }

        var lowerCount = hull.Count + 1;

        for (var i = sorted.Count - 2; i >= 0; i--)
        {
            var point = sorted[i];

            while (hull.Count >= lowerCount && Cross(hull[^2], hull[^1], point) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(point);
        }

        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static float Cross(OcrPoint a, OcrPoint b, OcrPoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static float MeanProbability(
        float[] probability,
        int width,
        int height,
        (OcrPoint TopLeft, OcrPoint TopRight, OcrPoint BottomRight, OcrPoint BottomLeft, float Width, float Height) rect)
    {
        var corners = new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft };

        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var minY = int.MaxValue;
        var maxY = int.MinValue;

        foreach (var corner in corners)
        {
            minX = Math.Min(minX, (int)MathF.Floor(corner.X));
            maxX = Math.Max(maxX, (int)MathF.Ceiling(corner.X));
            minY = Math.Min(minY, (int)MathF.Floor(corner.Y));
            maxY = Math.Max(maxY, (int)MathF.Ceiling(corner.Y));
        }

        minX = Math.Clamp(minX, 0, width - 1);
        maxX = Math.Clamp(maxX, 0, width - 1);
        minY = Math.Clamp(minY, 0, height - 1);
        maxY = Math.Clamp(maxY, 0, height - 1);

        double sum = 0;
        var count = 0;

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (!InsideConvexQuad(corners, x + 0.5f, y + 0.5f))
                {
                    continue;
                }

                sum += probability[y * width + x];
                count++;
            }
        }

        return count == 0 ? 0 : (float)(sum / count);
    }

    private static bool InsideConvexQuad(OcrPoint[] corners, float x, float y)
    {
        var positive = false;
        var negative = false;

        for (var i = 0; i < corners.Length; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % corners.Length];
            var cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

            if (cross > 1e-4f) positive = true;
            else if (cross < -1e-4f) negative = true;

            if (positive && negative)
            {
                return false;
            }
        }

        return true;
    }
}
