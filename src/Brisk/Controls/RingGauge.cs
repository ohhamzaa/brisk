using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Brisk.Controls;

/// <summary>Circular 0-100 gauge. Colour shifts to warning/danger brushes at high values.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, OnValueChanged));

    /// <summary>What's actually drawn: eases toward <see cref="Value"/> so the ring sweeps instead of jumping.</summary>
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly IEasingFunction Ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (RingGauge)d;
        double to = (double)e.NewValue;
        if (!Anim.Enabled) { g.BeginAnimation(ShownProperty, null); g.SetValue(ShownProperty, to); return; }
        // First value sweeps up from zero; later ones glide over most of the 1 s refresh.
        double ms = (double)g.GetValue(ShownProperty) == 0 ? 900 : 420;
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
        Timeline.SetDesiredFrameRate(a, 30); // smooth enough for a ring, half the redraws
        g.BeginAnimation(ShownProperty, a);
    }

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarnBrushProperty = DependencyProperty.Register(
        nameof(WarnBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DangerBrushProperty = DependencyProperty.Register(
        nameof(DangerBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.IndianRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush WarnBrush { get => (Brush)GetValue(WarnBrushProperty); set => SetValue(WarnBrushProperty, value); }
    public Brush DangerBrush { get => (Brush)GetValue(DangerBrushProperty); set => SetValue(DangerBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), c, r, r);

        double v = Math.Clamp((double)GetValue(ShownProperty), 0, 100);
        if (v <= 0.05) return;
        double target = Math.Clamp(Value, 0, 100); // colour follows the real value, not the animation
        var brush = target >= 90 ? DangerBrush : target >= 75 ? WarnBrush : FillBrush;
        var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        if (v >= 99.95) { dc.DrawEllipse(null, pen, c, r, r); return; }

        double angle = v / 100 * 360;
        Point P(double deg)
        {
            double rad = (deg - 90) * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            ctx.ArcTo(P(angle), new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
