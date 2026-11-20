using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DeleteMan.App.Controls;

/// <summary>
/// One ribbon: from the bottom edge of <paramref name="Anchor"/> (the expanded segment in the row above) to the
/// top edge of <paramref name="ChildBar"/> (the whole bar of the row it opened), in <paramref name="Edge"/>.
/// </summary>
/// <param name="Anchor">The parent segment the ribbon starts from.</param>
/// <param name="ChildBar">The child row's bar the ribbon fans out to.</param>
/// <param name="Edge">Ribbon colour (normally the child row's edge colour = the anchor segment colour).</param>
public sealed record RibbonSpec(FrameworkElement Anchor, FrameworkElement ChildBar, Color Edge);

/// <summary>
/// Draws the bezier "ribbons" that connect each row to the segment above it that it expands. Place it behind the
/// rows (same cell, earlier in z-order) so it shows in the gaps between them; it never takes input.
/// Geometry is measured with <c>TransformToVisual(this)</c> and drawn once per frame at most: every trigger
/// (explicit <see cref="Invalidate"/>, size changes, layout passes) only marks the layer dirty, and the redraw
/// happens on the next <see cref="CompositionTarget.Rendering"/>. A redraw that produces the same geometry
/// touches nothing, so the layout pass it would otherwise cause cannot loop.
/// </summary>
public sealed partial class RibbonLayer : Canvas
{
    /// <summary>Fill opacity of a ribbon (the web UI's fill-opacity 0.25).</summary>
    private const double FillAlpha = 0.25;

    /// <summary>Stroke opacity of a ribbon (stroke-opacity 0.7).</summary>
    private const double StrokeAlpha = 0.7;

    private readonly List<Path> _paths = [];
    private readonly List<double[]> _lastGeometry = [];
    private IReadOnlyList<RibbonSpec> _ribbons = Array.Empty<RibbonSpec>();
    private bool _dirty;
    private bool _renderingHooked;

    /// <summary>Creates an empty layer.</summary>
    public RibbonLayer()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => Invalidate();
        // Rows can move without this layer changing size (a row above grows, a segment re-proportions). Only mark
        // dirty here; drawing inside LayoutUpdated would itself invalidate layout.
        LayoutUpdated += (_, _) => Invalidate();
        Loaded += (_, _) => Invalidate();
        Unloaded += (_, _) => Unhook();
    }

    /// <summary>Replaces the set of ribbons; one <see cref="Path"/> per ribbon is reused across redraws.</summary>
    public void SetRibbons(IReadOnlyList<RibbonSpec> ribbons)
    {
        _ribbons = ribbons;
        while (_paths.Count > ribbons.Count)
        {
            Children.Remove(_paths[^1]);
            _paths.RemoveAt(_paths.Count - 1);
            _lastGeometry.RemoveAt(_lastGeometry.Count - 1);
        }
        while (_paths.Count < ribbons.Count)
        {
            var path = new Path { StrokeThickness = 1, IsHitTestVisible = false };
            _paths.Add(path);
            _lastGeometry.Add([]);
            Children.Add(path);
        }
        for (int i = 0; i < ribbons.Count; i++)
        {
            Color c = ribbons[i].Edge;
            _paths[i].Fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * FillAlpha), c.R, c.G, c.B));
            _paths[i].Stroke = new SolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * StrokeAlpha), c.R, c.G, c.B));
            _lastGeometry[i] = [];
        }
        Invalidate();
    }

    /// <summary>Requests a redraw on the next frame (coalesced; cheap to call often).</summary>
    public void Invalidate()
    {
        _dirty = true;
        if (!_renderingHooked && IsLoaded)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        }
    }

    private void Unhook()
    {
        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        Unhook();
        if (_dirty)
        {
            _dirty = false;
            Draw();
        }
    }

    private void Draw()
    {
        for (int i = 0; i < _ribbons.Count; i++)
        {
            RibbonSpec spec = _ribbons[i];
            Path path = _paths[i];
            if (!TryMeasure(spec, out double[] g))
            {
                path.Visibility = Visibility.Collapsed;
                _lastGeometry[i] = [];
                continue;
            }
            path.Visibility = Visibility.Visible;
            if (_lastGeometry[i].AsSpan().SequenceEqual(g))
            {
                continue;
            }
            _lastGeometry[i] = g;
            path.Data = Build(g[0], g[1], g[2], g[3], g[4], g[5]);
        }
    }

    /// <summary>Returns {xa, xb, y1, xc, xd, y2} in layer coordinates, or false when an end is not laid out.</summary>
    private bool TryMeasure(RibbonSpec spec, out double[] geometry)
    {
        geometry = [];
        FrameworkElement anchor = spec.Anchor;
        FrameworkElement bar = spec.ChildBar;
        if (!anchor.IsLoaded || !bar.IsLoaded || anchor.ActualWidth <= 0 || bar.ActualWidth <= 0
            || anchor.Visibility != Visibility.Visible || bar.Visibility != Visibility.Visible)
        {
            return false;
        }
        Rect a, c;
        try
        {
            a = anchor.TransformToVisual(this).TransformBounds(new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight));
            c = bar.TransformToVisual(this).TransformBounds(new Rect(0, 0, bar.ActualWidth, bar.ActualHeight));
        }
        catch (ArgumentException)
        {
            // Not in the same visual tree (being removed); skip until the next SetRibbons.
            return false;
        }
        // Pull each end a hair inward so the 1 px stroke (centred on the path) sits just inside the bar edges
        // rather than overshooting them. Never cross the midpoint, so thin segments still render.
        (double xa, double xb) = Inset(a.Left, a.Right);
        (double xc, double xd) = Inset(c.Left, c.Right);
        geometry = [xa, xb, a.Bottom, xc, xd, c.Top];
        return true;
    }

    private static (double Lo, double Hi) Inset(double lo, double hi)
    {
        double m = Math.Min(1.5, (hi - lo) / 2);
        return (lo + m, hi - m);
    }

    /// <summary>
    /// The web UI's path: M xa,y1 C xa,ym xc,ym xc,y2 L xd,y2 C xd,ym xb,ym xb,y1 Z, with both bezier control
    /// points at the vertical midpoint so each side leaves and arrives vertically.
    /// </summary>
    private static PathGeometry Build(double xa, double xb, double y1, double xc, double xd, double y2)
    {
        double ym = (y1 + y2) / 2;
        var figure = new PathFigure { StartPoint = new Point(xa, y1), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new BezierSegment { Point1 = new Point(xa, ym), Point2 = new Point(xc, ym), Point3 = new Point(xc, y2) });
        figure.Segments.Add(new LineSegment { Point = new Point(xd, y2) });
        figure.Segments.Add(new BezierSegment { Point1 = new Point(xd, ym), Point2 = new Point(xb, ym), Point3 = new Point(xb, y1) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
