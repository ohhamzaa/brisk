using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brisk.Services;

namespace Brisk.Controls;

/// <summary>
/// 7 rows (today on top) x 24 hours. Each recorded slowdown is a mark at the time it happened,
/// as wide as it lasted (min 4 px), coloured by what caused it. Hover a mark to read it.
/// </summary>
public sealed class LagTimeline : FrameworkElement
{
    public static readonly DependencyProperty EventsProperty = DependencyProperty.Register(
        nameof(Events), typeof(IReadOnlyList<LagEvent>), typeof(LagTimeline),
        new FrameworkPropertyMetadata(Array.Empty<LagEvent>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<LagEvent> Events { get => (IReadOnlyList<LagEvent>)GetValue(EventsProperty); set => SetValue(EventsProperty, value); }

    private const double LabelW = 78, RowH = 24, AxisH = 20, Days = 7;
    private readonly List<(Rect Box, LagEvent Ev)> _hits = [];
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };

    public LagTimeline()
    {
        Height = RowH * Days + AxisH;
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 0);
        _tip.Opened += (_, _) => { if (_tip.Content == null) _tip.IsOpen = false; };
    }

    private Brush B(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    public Brush KindBrush(LagEvent e) =>
        e.Kinds.Contains("Network") && e.Kinds.Count == 1 ? B("InfoBrush")
        : e.Kinds.Contains("Memory") || e.Kinds.Contains("GPU memory") ? B("WarnBrush")
        : e.Kinds.Contains("Disk") ? B("PurpleBrush")
        : B("DangerBrush");

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        double w = ActualWidth;
        if (w <= LabelW + 20) return;
        double plotW = w - LabelW;
        var dim = B("TextFaintBrush");
        var track = B("Surface2Brush");
        var grid = new Pen(B("BorderBrush"), 1);
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Text(string s, Brush b) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 11, b, dpi);

        for (int h = 0; h <= 24; h += 6)
        {
            double x = LabelW + plotW * h / 24;
            dc.DrawLine(grid, new Point(x, 0), new Point(x, RowH * Days));
            var t = Text(h == 24 ? "24:00" : $"{h:00}:00", dim);
            double tx = Math.Clamp(x - t.Width / 2, LabelW, w - t.Width);
            dc.DrawText(t, new Point(tx, RowH * Days + 4));
        }

        for (int d = 0; d < Days; d++)
        {
            var day = DateTime.Today.AddDays(-d);
            double y = d * RowH;
            string label = d == 0 ? "Today" : d == 1 ? "Yesterday" : day.ToString("ddd d", CultureInfo.CurrentCulture);
            var lt = Text(label, d == 0 ? B("TextDimBrush") : dim);
            dc.DrawText(lt, new Point(0, y + (RowH - lt.Height) / 2));
            dc.DrawRoundedRectangle(track, null, new Rect(LabelW, y + RowH / 2 - 3, plotW, 6), 3, 3);

            foreach (var e in Events.Where(e => e.Start.Date == day))
            {
                double x = LabelW + plotW * e.Start.TimeOfDay.TotalSeconds / 86400;
                double bw = Math.Max(4, plotW * e.Seconds / 86400);
                var box = new Rect(Math.Min(x, w - bw), y + 5, bw, RowH - 10);
                dc.DrawRoundedRectangle(KindBrush(e), null, box, 2, 2);
                _hits.Add((Rect.Inflate(box, 3, 2), e));
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        var hit = _hits.LastOrDefault(h => h.Box.Contains(p));
        if (hit.Ev == null) { _tip.Content = null; _tip.IsOpen = false; Cursor = null; return; }
        var ev = hit.Ev;
        string dur = ev.Seconds >= 60 ? $"{ev.Seconds / 60:0} min {ev.Seconds % 60:0} s" : $"{ev.Seconds:0} s";
        _tip.Content = $"{ev.Start:t}  ·  {dur}  ·  {string.Join(" + ", ev.Kinds)}\n{ev.Headline}";
        _tip.IsOpen = true;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.IsOpen = false;
    }
}
