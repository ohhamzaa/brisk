using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Brisk.Controls;

/// <summary>
/// Entrance animations as attached properties:  controls:Anim.Delay="80"  (rise + fade)
/// or  controls:Anim.Pop="True"  (scale pop). Plays every time the element becomes visible.
/// Honours Windows' "Animation effects" setting.
/// </summary>
public static class Anim
{
    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(int), typeof(Anim), new PropertyMetadata(-1, OnAttach));

    public static readonly DependencyProperty PopProperty = DependencyProperty.RegisterAttached(
        "Pop", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnAttach));

    public static int GetDelay(DependencyObject d) => (int)d.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject d, int v) => d.SetValue(DelayProperty, v);
    public static bool GetPop(DependencyObject d) => (bool)d.GetValue(PopProperty);
    public static void SetPop(DependencyObject d, bool v) => d.SetValue(PopProperty, v);

    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    private static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction Back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(Anim), new PropertyMetadata(false));

    private static void OnAttach(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || (bool)fe.GetValue(HookedProperty)) return;
        fe.SetValue(HookedProperty, true); // Delay + Pop on one element must only hook once
        fe.Loaded += (_, _) => Play(fe);
        fe.IsVisibleChanged += (_, args) => { if (args.NewValue is true) Play(fe); };
    }

    public static void Play(FrameworkElement fe)
    {
        if (!Enabled) return;
        bool pop = GetPop(fe);
        int delay = Math.Max(0, GetDelay(fe));
        var begin = TimeSpan.FromMilliseconds(delay);

        fe.BeginAnimation(UIElement.OpacityProperty, Hold(0, 1, begin, 280, Out));
        if (pop)
        {
            var st = new ScaleTransform(1, 1);
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
            fe.RenderTransform = st;
            var a = Hold(0.85, 1, begin, 380, Back);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        else
        {
            var tt = new TranslateTransform();
            fe.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.YProperty, Hold(14, 0, begin, 420, Out));
        }
    }

    /// <summary>Holds <paramref name="from"/> during the delay (no flash of the final state), then eases to <paramref name="to"/>.</summary>
    private static DoubleAnimationUsingKeyFrames Hold(double from, double to, TimeSpan delay, int ms, IEasingFunction ease)
    {
        var a = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > TimeSpan.Zero) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(ms)), ease));
        a.Freeze();
        return a;
    }
}

/// <summary>
/// Looping decorations that cost nothing when you can't see them:  controls:Loop.Kind="heartbeat|trace|ripple".
/// Clocks only run while the element is visible and its window isn't minimised, and the heartbeat is a
/// one-shot animation re-fired by a timer, so no animation clock ticks during its pause.
/// </summary>
public static class Loop
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(string), typeof(Loop), new PropertyMetadata(null, OnKind));

    public static string? GetKind(DependencyObject d) => (string?)d.GetValue(KindProperty);
    public static void SetKind(DependencyObject d, string? v) => d.SetValue(KindProperty, v);

    private const int Fps = 30;

    private static void OnKind(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not string kind) return;
        DispatcherTimerHolder? beat = null;

        void Start()
        {
            if (!Anim.Enabled || !fe.IsVisible) return;
            switch (kind)
            {
                case "heartbeat" when fe is System.Windows.Shapes.Shape s:
                    s.StrokeDashArray = [200, 200];
                    beat ??= new DispatcherTimerHolder(TimeSpan.FromSeconds(3.2), () => Beat(s));
                    Beat(s);
                    beat.Start();
                    break;
                case "trace" when fe is System.Windows.Shapes.Shape s:
                    s.StrokeDashArray = [34, 200];
                    var trace = new DoubleAnimation(40, -34, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever };
                    Timeline.SetDesiredFrameRate(trace, Fps);
                    s.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, trace);
                    break;
                case "ripple":
                    var st = new ScaleTransform();
                    fe.RenderTransformOrigin = new Point(0.5, 0.5);
                    fe.RenderTransform = st;
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    var grow = new DoubleAnimation(1, 1.55, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
                    var fade = new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever };
                    Timeline.SetDesiredFrameRate(grow, Fps);
                    Timeline.SetDesiredFrameRate(fade, Fps);
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                    fe.BeginAnimation(UIElement.OpacityProperty, fade);
                    break;
            }
        }

        void Stop()
        {
            beat?.Stop();
            if (fe is System.Windows.Shapes.Shape s) s.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
            fe.BeginAnimation(UIElement.OpacityProperty, null);
            if (fe.RenderTransform is ScaleTransform st)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            if (kind == "ripple") fe.Opacity = 0;
        }

        fe.IsVisibleChanged += (_, a) => { if (a.NewValue is true) Start(); else Stop(); };
        fe.Unloaded += (_, _) => Stop();
        fe.Loaded += (_, _) =>
        {
            Start();
            if (Window.GetWindow(fe) is { } w)
                w.StateChanged += (_, _) => { if (w.WindowState == WindowState.Minimized) Stop(); else if (fe.IsVisible) Start(); };
        };
    }

    private static void Beat(System.Windows.Shapes.Shape s)
    {
        var a = new DoubleAnimation(200, 0, TimeSpan.FromSeconds(1.1)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        Timeline.SetDesiredFrameRate(a, Fps);
        s.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, a);
    }

    private sealed class DispatcherTimerHolder(TimeSpan interval, Action tick)
    {
        private readonly System.Windows.Threading.DispatcherTimer _t = CreateTimer(interval, tick);
        private static System.Windows.Threading.DispatcherTimer CreateTimer(TimeSpan i, Action tick)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = i };
            t.Tick += (_, _) => tick();
            return t;
        }
        public void Start() => _t.Start();
        public void Stop() => _t.Stop();
    }
}

/// <summary>ContentControl that fades/slides its content in whenever the page changes.</summary>
public sealed class AnimatedContentControl : ContentControl
{
    private readonly TranslateTransform _shift = new();

    public AnimatedContentControl() => RenderTransform = _shift;

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (!Anim.Enabled || oldContent == null) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }
}
