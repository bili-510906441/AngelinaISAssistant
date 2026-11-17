using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AngelinaISAssistant;

/// <summary>搜索命中的圆角彩虹框（彩虹沿边框滚动）。</summary>
internal sealed class RainbowAdorner : Adorner
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x4D, 0x4F),
        Color.FromRgb(0xFF, 0x9F, 0x1C),
        Color.FromRgb(0xFF, 0xD6, 0x0A),
        Color.FromRgb(0x3D, 0xDC, 0x84),
        Color.FromRgb(0x32, 0xD1, 0xF0),
        Color.FromRgb(0x4C, 0x8B, 0xF5),
        Color.FromRgb(0xA8, 0x5C, 0xF5),
        Color.FromRgb(0xFF, 0x4D, 0x4F)
    };

    private readonly Pen _pen;

    public RainbowAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            SpreadMethod = GradientSpreadMethod.Repeat
        };

        for (var i = 0; i < Palette.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop(Palette[i], (double)i / (Palette.Length - 1)));
        }

        var shift = new TranslateTransform();
        brush.RelativeTransform = shift;

        var animation = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.4))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        shift.BeginAnimation(TranslateTransform.XProperty, animation);

        _pen = new Pen(brush, 2);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var rect = new Rect(AdornedElement.RenderSize);
        rect.Inflate(-1, -1);

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        drawingContext.DrawRoundedRectangle(null, _pen, rect, 9, 9);
    }
}
