using System.Windows;
using System.Windows.Media;

namespace Brisk.Controls;

/// <summary>
/// Live area chart on a fixed 0-100 scale: smoothed line, soft vertical fade, faint gridlines,
/// and a dot on the newest sample. Feed it a new array to redraw.
/// </summary>
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

    private static readonly Pen GridPen = CreateGridPen();
    private static Pen CreateGridPen()
    {
        var p = new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1) { DashStyle = new DashStyle([2, 4], 0) };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        const double top = 6, bottom = 2; // headroom so the dot and line never clip
        double plotH = h - top - bottom;
        foreach (var level in new[] { 25.0, 50, 75 })
        {
            double gy = Math.Round(top + plotH * (1 - level / 100)) + 0.5;
            dc.DrawLine(GridPen, new Point(0, gy), new Point(w, gy));
        }

        var v = Values;
        if (v.Length < 2) return;
        double step = w / (Capacity - 1);
        double x0 = w - (v.Length - 1) * step; // newest sample sits on the right edge
        var pts = new Point[v.Length];
        for (int i = 0; i < v.Length; i++)
            pts[i] = new Point(x0 + i * step, top + plotH * (1 - Math.Clamp(v[i], 0, 100) / 100));

        var line = Smooth(pts, closeToBottom: false, h);
        var area = Smooth(pts, closeToBottom: true, h);

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.LimeGreen;
        var fill = new LinearGradientBrush(Color.FromArgb(70, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);

        var last = pts[^1];
        var halo = new SolidColorBrush(Color.FromArgb(55, color.R, color.G, color.B));
        halo.Freeze();
        dc.DrawEllipse(halo, null, last, 6, 6);
        dc.DrawEllipse(Stroke, null, last, 3, 3);
    }

    /// <summary>Catmull-Rom spline through the points, as cubic Béziers, clamped to the plot.</summary>
    private static StreamGeometry Smooth(Point[] p, bool closeToBottom, double h)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            if (closeToBottom)
            {
                ctx.BeginFigure(new Point(p[0].X, h), true, true);
                ctx.LineTo(p[0], true, true);
            }
            else ctx.BeginFigure(p[0], false, false);

            for (int i = 0; i < p.Length - 1; i++)
            {
                Point p0 = p[Math.Max(0, i - 1)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(p.Length - 1, i + 2)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                // keep control points inside the band between the two samples (no overshoot spikes)
                c1.Y = Math.Clamp(c1.Y, Math.Min(p1.Y, p2.Y), Math.Max(p1.Y, p2.Y));
                c2.Y = Math.Clamp(c2.Y, Math.Min(p1.Y, p2.Y), Math.Max(p1.Y, p2.Y));
                ctx.BezierTo(c1, c2, p2, true, true);
            }
            if (closeToBottom) ctx.LineTo(new Point(p[^1].X, h), true, true);
        }
        g.Freeze();
        return g;
    }
}
