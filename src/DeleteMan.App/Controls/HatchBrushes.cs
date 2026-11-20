using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeleteMan.App.Controls;

/// <summary>
/// Builds the diagonal stripe geometry used for hatched segments, error slivers and indeterminate progress:
/// the equivalent of CSS <c>repeating-linear-gradient(135deg, A 0 s, B s 2s)</c>, stripes running from
/// bottom-left to top-right, each band <c>s</c> px wide measured across the stripes.
/// </summary>
/// <remarks>
/// A repeating <see cref="LinearGradientBrush"/> (<c>SpreadMethod=Repeat</c>, <c>MappingMode=Absolute</c>)
/// draws the stripes, but in Windows App SDK 2.6 it also broke composition of its surroundings: every
/// element in the same card was drawn at half brightness. Stripes are therefore real geometry
/// (parallelograms) clipped to the element, rendered by <see cref="StripeFill"/>.
/// </remarks>
public static class HatchBrushes
{
    /// <summary>Band width of the normal hatch (a full period is twice this).</summary>
    public const double StripeWidth = 6;

    /// <summary>Band width of the error hatch.</summary>
    public const double ErrorStripeWidth = 4;

    /// <summary>Band width of the animated indeterminate-progress stripes.</summary>
    public const double ProgressStripeWidth = 10;

    /// <summary>
    /// Horizontal length of one period for band width <paramref name="stripe"/>: the 45° period of 2·s measured
    /// along x is 2·s·√2, so sliding stripes by this distance loops seamlessly.
    /// </summary>
    public static double PeriodX(double stripe) => stripe * 2 * Math.Sqrt(2);

    /// <summary>
    /// Geometry of one set of bands (A when <paramref name="phase"/> is 0, B when it is 0.5) covering
    /// x in [<paramref name="left"/>, <paramref name="right"/>] and y in [0, <paramref name="height"/>].
    /// </summary>
    /// <param name="left">Leftmost x to cover (may be negative so a sliding layer has spare stripes).</param>
    /// <param name="right">Rightmost x to cover.</param>
    /// <param name="height">Height to cover.</param>
    /// <param name="stripe">Band width across the stripes.</param>
    /// <param name="phase">0 for the first colour's bands, 0.5 for the second's.</param>
    public static PathGeometry Bands(double left, double right, double height, double stripe, double phase)
    {
        double period = PeriodX(stripe);
        double band = period / 2;
        var geometry = new PathGeometry();
        // Bands are the strips between lines x + y = c1 and x + y = c2; start far enough left that the strip
        // reaching the bottom-left corner is included.
        double start = Math.Floor((left - height) / period) * period + (phase * period);
        for (double c = start; c < right + height; c += period)
        {
            double c1 = c, c2 = c + band;
            var figure = new PathFigure { StartPoint = new Point(c1, 0), IsClosed = true, IsFilled = true };
            figure.Segments.Add(new LineSegment { Point = new Point(c2, 0) });
            figure.Segments.Add(new LineSegment { Point = new Point(c2 - height, height) });
            figure.Segments.Add(new LineSegment { Point = new Point(c1 - height, height) });
            geometry.Figures.Add(figure);
        }
        return geometry;
    }
}
