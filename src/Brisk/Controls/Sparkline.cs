using System.Windows;
using System.Windows.Media;

namespace Brisk.Controls;

/// <summary>Tiny live line chart over a fixed 0-100 scale. Feed it a new array to redraw.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(Sparkline),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[] Values { get => (double[])GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var v = Values;
        double w = ActualWidth, h = ActualHeight;
        if (v.Length < 2 || w <= 0 || h <= 0) return;

        double step = w / (Capacity - 1);
        double x0 = w - (v.Length - 1) * step; // newest sample sits on the right edge
        Point Pt(int i) => new(x0 + i * step, h - Math.Clamp(v[i], 0, 100) / 100 * (h - 2) - 1);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(Pt(0), false, false);
            for (int i = 1; i < v.Length; i++) ctx.LineTo(Pt(i), true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < v.Length; i++) ctx.LineTo(Pt(i), true, true);
            ctx.LineTo(new Point(w, h), true, true);
        }
        area.Freeze();

        Brush fill = Stroke.Clone();
        fill.Opacity = 0.12;
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
