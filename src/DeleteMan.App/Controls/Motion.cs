using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace DeleteMan.App.Controls;

/// <summary>Shared animation helpers that honour the Windows "Animation effects" setting.</summary>
internal static class Motion
{
    private static readonly UISettings Settings = new();

    /// <summary>The web UI's ROW_ANIM_MS: how long a row takes to grow open or shrink closed; segments glide to a new proportion in the same time.</summary>
    public static readonly TimeSpan RowDuration = TimeSpan.FromMilliseconds(190);

    /// <summary>First control point of the web UI's ROW_EASE, cubic-bezier(.33, 0, .2, 1).</summary>
    public static readonly Point RowEaseIn = new(0.33, 0);

    /// <summary>Second control point of ROW_EASE.</summary>
    public static readonly Point RowEaseOut = new(0.2, 1);

    /// <summary>
    /// True when the user allows animations (Settings, Accessibility, Visual effects). Read each time an
    /// animation would start so a change applies to the next one without a restart.
    /// </summary>
    public static bool Enabled => Settings.AnimationsEnabled;

    /// <summary>The current time for <see cref="Tween"/>s, in <see cref="Stopwatch"/> ticks.</summary>
    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>ROW_EASE as a function: progress 0..1 in, eased progress 0..1 out.</summary>
    public static double RowEase(double t) => CubicBezier(t, RowEaseIn.X, RowEaseIn.Y, RowEaseOut.X, RowEaseOut.Y);

    /// <summary>The identity easing (CSS <c>linear</c>).</summary>
    public static double Linear(double t) => t;

    /// <summary>
    /// Evaluates the CSS <c>cubic-bezier(x1, y1, x2, y2)</c> timing function at <paramref name="t"/>: finds the
    /// curve parameter whose x is <paramref name="t"/> (Newton steps with a bisection fallback, as browsers do)
    /// and returns the y there.
    /// </summary>
    public static double CubicBezier(double t, double x1, double y1, double x2, double y2)
    {
        if (t <= 0)
        {
            return 0;
        }
        if (t >= 1)
        {
            return 1;
        }
        double s = t;
        for (int i = 0; i < 8; i++)
        {
            double x = Bezier(s, x1, x2) - t;
            if (Math.Abs(x) < 1e-6)
            {
                return Bezier(s, y1, y2);
            }
            double dx = BezierSlope(s, x1, x2);
            if (Math.Abs(dx) < 1e-6)
            {
                break;
            }
            s -= x / dx;
        }
        double lo = 0, hi = 1;
        s = t;
        while (hi - lo > 1e-6)
        {
            double x = Bezier(s, x1, x2);
            if (x < t)
            {
                lo = s;
            }
            else
            {
                hi = s;
            }
            s = (lo + hi) / 2;
        }
        return Bezier(s, y1, y2);
    }

    // One coordinate of a cubic bezier from (0,0) to (1,1) with control coordinates a and b.
    private static double Bezier(double s, double a, double b)
    {
        double u = 1 - s;
        return (3 * u * u * s * a) + (3 * u * s * s * b) + (s * s * s);
    }

    private static double BezierSlope(double s, double a, double b)
    {
        double u = 1 - s;
        return (3 * u * u * a) + (6 * u * s * (b - a)) + (3 * s * s * (1 - b));
    }

    /// <summary>
    /// An endless opacity pulse 1 → <paramref name="low"/> → 1 on <paramref name="target"/>; one full cycle takes
    /// <paramref name="cycle"/> (the web UI's <c>pulse</c> keyframes: 1.6 s, lowest at the half-way point).
    /// </summary>
    public static Storyboard Pulse(UIElement target, double low, TimeSpan cycle)
    {
        var animation = new DoubleAnimation
        {
            From = 1,
            To = low,
            Duration = new Duration(cycle / 2),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        return storyboard;
    }

    /// <summary>A linear, endless 0 → <paramref name="distance"/> slide of a translate transform's X.</summary>
    public static Storyboard Slide(DependencyObject translateTransform, double distance, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = distance,
            Duration = new Duration(duration),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(animation, translateTransform);
        Storyboard.SetTargetProperty(animation, "X");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        return storyboard;
    }
}

/// <summary>
/// One value that glides toward a target instead of jumping: a CSS <c>transition</c> for values that XAML
/// storyboards cannot reach (a panel's own layout numbers, a width derived from a fraction). The owner reads
/// <see cref="ValueAt"/> during layout and keeps asking for layout passes while <see cref="IsActive"/>; a new
/// target set mid-flight starts from the value shown at that moment, so there is never a jump.
/// </summary>
internal sealed class Tween
{
    private readonly long _ticks;
    private readonly Func<double, double> _ease;
    private double _from;
    private double _to;
    private long _start = long.MinValue / 2;

    /// <param name="duration">How long one glide takes.</param>
    /// <param name="ease">Timing function (progress 0..1 in, eased 0..1 out).</param>
    /// <param name="value">Initial value, shown at once.</param>
    public Tween(TimeSpan duration, Func<double, double> ease, double value = 0)
    {
        _ticks = (long)(duration.TotalSeconds * Stopwatch.Frequency);
        _ease = ease;
        _from = _to = value;
    }

    /// <summary>Where the value is heading (the last target set).</summary>
    public double Target => _to;

    /// <summary>True while the shown value still differs from <see cref="Target"/> at <paramref name="now"/>.</summary>
    public bool IsActive(long now) => now - _start < _ticks && _from != _to;

    /// <summary>Shows <paramref name="value"/> immediately (animations disabled, or a first appearance).</summary>
    public void Snap(double value)
    {
        _from = _to = value;
        _start = long.MinValue / 2;
    }

    /// <summary>Starts gliding from the value shown at <paramref name="now"/> to <paramref name="to"/>; a no-op when already heading there.</summary>
    public void Retarget(double to, long now)
    {
        if (to == _to)
        {
            return;
        }
        _from = ValueAt(now);
        _to = to;
        _start = now;
    }

    /// <summary>The value to show at <paramref name="now"/>.</summary>
    public double ValueAt(long now)
    {
        if (_ticks <= 0 || _from == _to)
        {
            return _to;
        }
        double t = (now - _start) / (double)_ticks;
        if (t >= 1)
        {
            return _to;
        }
        if (t <= 0)
        {
            return _from;
        }
        return _from + ((_to - _from) * _ease(t));
    }
}
