using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeleteMan.App.Controls;

/// <summary>
/// Lays children out horizontally at percentage positions: each child's <see cref="GetLeft">Left</see> and
/// <see cref="GetSpan">Span</see> (width) are percentages (0..100) of the panel width, and every child gets the full
/// panel height. Both edges of every child are snapped to whole device pixels, so segments whose percentages
/// touch also touch on screen (no hairline gaps, no overlap) at any DPI.
/// </summary>
/// <remarks>
/// Changes glide instead of jumping: when a child's percentages change (a scan refines the proportions every
/// poll), the panel eases the shown position and width from the old values to the new ones over
/// <see cref="Motion.RowDuration"/> with the row open/close easing, re-laying itself out every frame meanwhile.
/// A child that is new to a panel that already has children grows out of the seam it is inserted at; a child
/// removed with <see cref="RemoveAnimated"/> shrinks into the seam it leaves, then detaches itself. Because every
/// child interpolates with the same clock, two layouts that do not overlap never overlap in between either,
/// unless children swap order, in which case they slide across each other. This is a layout animation on
/// purpose: a ribbon anchored on a segment follows it every frame. Nothing animates while the Windows
/// "Animation effects" setting is off.
/// </remarks>
public sealed partial class ProportionalPanel : Panel
{
    /// <summary>Identifies the Left attached property (percent of the panel width).</summary>
    public static readonly DependencyProperty LeftProperty = DependencyProperty.RegisterAttached(
        "Left", typeof(double), typeof(ProportionalPanel), new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    /// <summary>
    /// Identifies the Span attached property: the child's width in percent of the panel width. (Not named
    /// "Width": the XAML compiler resolves <c>ProportionalPanel.Width</c> to the inherited FrameworkElement.Width
    /// and fails, so the attached form needs a name of its own.)
    /// </summary>
    public static readonly DependencyProperty SpanProperty = DependencyProperty.RegisterAttached(
        "Span", typeof(double), typeof(ProportionalPanel), new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    private readonly Dictionary<UIElement, Glide> _glides = new(ReferenceEqualityComparer.Instance);
    private readonly List<UIElement> _finished = [];
    private bool _renderingHooked;
    private bool _active;
    private long _now;

    /// <summary>Creates an empty panel.</summary>
    public ProportionalPanel()
    {
        Loaded += (_, _) =>
        {
            if (_active || _finished.Count > 0)
            {
                Hook();
            }
        };
        Unloaded += (_, _) =>
        {
            Unhook();
            FinishAll();
        };
    }

    /// <summary>Gets the left edge of <paramref name="element"/> in percent of the panel width.</summary>
    public static double GetLeft(UIElement element) => (double)element.GetValue(LeftProperty);

    /// <summary>Sets the left edge of <paramref name="element"/> in percent of the panel width.</summary>
    public static void SetLeft(UIElement element, double value) => element.SetValue(LeftProperty, value);

    /// <summary>Gets the width of <paramref name="element"/> in percent of the panel width.</summary>
    public static double GetSpan(UIElement element) => (double)element.GetValue(SpanProperty);

    /// <summary>Sets the width of <paramref name="element"/> in percent of the panel width.</summary>
    public static void SetSpan(UIElement element, double value) => element.SetValue(SpanProperty, value);

    /// <summary>True for a child that is shrinking away after <see cref="RemoveAnimated"/>.</summary>
    public bool IsLeaving(UIElement child) => _glides.TryGetValue(child, out Glide? g) && g.Leaving;

    /// <summary>
    /// Puts <paramref name="child"/> at <paramref name="index"/> among the children that are not leaving, inserting
    /// it if it is not a child yet. Leaving children keep their place in the collection (and so their z-order and
    /// seam) until they have shrunk away, and are skipped when counting.
    /// </summary>
    public void PlaceLive(UIElement child, int index)
    {
        int current = Children.IndexOf(child);
        // The final index of the child = how many other children stay in front of it: every child (leaving or
        // not) up to the point where `index` live children have been passed.
        int live = 0;
        int target = 0;
        for (int k = 0; k < Children.Count && live < index; k++)
        {
            if (k == current)
            {
                continue;
            }
            if (!IsLeaving(Children[k]))
            {
                live++;
            }
            target++;
        }
        if (current < 0)
        {
            Children.Insert(target, child);
        }
        else if (current != target)
        {
            Children.Move((uint)current, (uint)target);
        }
    }

    /// <summary>
    /// Shrinks <paramref name="child"/> into the seam between its neighbours and then removes it (or removes it at
    /// once when animations are off). The child stops taking input immediately.
    /// </summary>
    public void RemoveAnimated(UIElement child)
    {
        if (!Children.Contains(child))
        {
            return;
        }
        if (!Motion.Enabled || !IsLoaded || !_glides.TryGetValue(child, out Glide? glide))
        {
            Children.Remove(child);
            _glides.Remove(child);
            return;
        }
        glide.Leaving = true;
        child.IsHitTestVisible = false;
        if (child is Control control)
        {
            control.IsTabStop = false;
        }
        InvalidateMeasure();
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Attached properties carry no "affects measure" metadata in WinUI; tell the owning panel ourselves.
        if (d is UIElement child && VisualTreeHelper.GetParent(child) is ProportionalPanel panel)
        {
            panel.InvalidateMeasure();
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        _now = Motion.Now;
        SyncGlides();
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double maxChildHeight = 0;
        foreach (UIElement child in Children)
        {
            Glide g = _glides[child];
            double childWidth = Math.Max(Percent(g.Span.ValueAt(_now)) * width, g.Leaving ? 0 : MinWidthOf(child));
            child.Measure(new Size(childWidth, availableSize.Height));
            maxChildHeight = Math.Max(maxChildHeight, child.DesiredSize.Height);
        }
        double height = double.IsInfinity(availableSize.Height) ? maxChildHeight : availableSize.Height;
        if (_active || _finished.Count > 0)
        {
            Hook();
        }
        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        double total = finalSize.Width;
        foreach (UIElement child in Children)
        {
            if (!_glides.TryGetValue(child, out Glide? g))
            {
                // Arrange without a measure in between (a child added after the last measure): show the target.
                g = new Glide(GetLeft(child), GetSpan(child));
                _glides[child] = g;
            }
            double leftPct = Percent(g.Left.ValueAt(_now));
            double widthPct = Percent(g.Span.ValueAt(_now));
            // Snap both edges from the same percentages, so a child's right edge equals its neighbour's left edge.
            double x0 = Snap(leftPct * total, scale);
            double x1 = Snap(Math.Min(leftPct + widthPct, 1) * total, scale);
            double w = x1 - x0;
            double min = g.Leaving ? 0 : MinWidthOf(child);
            if (w < min)
            {
                // A sliver (an error segment, say) keeps its minimum width; keep it inside the panel.
                w = min;
                x0 = Math.Max(0, Math.Min(x0, total - w));
            }
            child.Arrange(new Rect(x0, 0, Math.Max(0, w), finalSize.Height));
        }
        return finalSize;
    }

    /// <summary>
    /// Brings every child's glide in line with its attached percentages (new targets start a glide from the value
    /// shown right now), gives new children and leaving children their seam, and notes whether anything is still
    /// moving. Runs once per measure, after the owner has updated all children, so a batch of changes starts
    /// together.
    /// </summary>
    private void SyncGlides()
    {
        bool animate = Motion.Enabled && IsLoaded;
        if (_glides.Count != Children.Count)
        {
            // A child removed behind our back (Children.Remove instead of RemoveAnimated) leaves a stale glide.
            var present = new HashSet<UIElement>(Children, ReferenceEqualityComparer.Instance);
            foreach (UIElement stale in _glides.Keys.Where(k => !present.Contains(k)).ToList())
            {
                _glides.Remove(stale);
            }
        }

        // Pass 1: live children head for their attached percentages. A child new to a non-empty panel starts as
        // a zero-width sliver at the seam it is inserted at: the right edge shown by the live child before it
        // (or the left edge shown by the one after it).
        bool hadChildren = _glides.Count > 0;
        for (int i = 0; i < Children.Count; i++)
        {
            UIElement child = Children[i];
            if (_glides.TryGetValue(child, out Glide? g))
            {
                if (!g.Leaving)
                {
                    g.Retarget(GetLeft(child), GetSpan(child), _now, animate);
                }
                continue;
            }
            g = new Glide(GetLeft(child), GetSpan(child));
            _glides[child] = g;
            if (!animate || !hadChildren)
            {
                continue;
            }
            double? seam = ShownRightEdgeBefore(i) ?? ShownLeftEdgeAfter(i);
            if (seam is { } s)
            {
                g.StartFrom(s, 0, _now);
            }
        }

        // Pass 2: leaving children collapse onto the seam between the live children around them.
        _active = false;
        _finished.Clear();
        for (int i = 0; i < Children.Count; i++)
        {
            UIElement child = Children[i];
            Glide g = _glides[child];
            if (g.Leaving)
            {
                double seam = TargetRightEdgeBefore(i) ?? TargetLeftEdgeAfter(i) ?? g.Left.ValueAt(_now);
                g.Retarget(seam, 0, _now, animate: true);
                if (!g.IsActive(_now))
                {
                    _finished.Add(child);
                    continue;
                }
            }
            _active |= g.IsActive(_now);
        }
    }

    private double? ShownRightEdgeBefore(int index)
    {
        for (int j = index - 1; j >= 0; j--)
        {
            if (_glides.TryGetValue(Children[j], out Glide? g) && !g.Leaving)
            {
                return g.Left.ValueAt(_now) + g.Span.ValueAt(_now);
            }
        }
        return null;
    }

    private double? ShownLeftEdgeAfter(int index)
    {
        for (int j = index + 1; j < Children.Count; j++)
        {
            if (_glides.TryGetValue(Children[j], out Glide? g) && !g.Leaving)
            {
                return g.Left.ValueAt(_now);
            }
        }
        return null;
    }

    private double? TargetRightEdgeBefore(int index)
    {
        for (int j = index - 1; j >= 0; j--)
        {
            if (!_glides[Children[j]].Leaving)
            {
                return GetLeft(Children[j]) + GetSpan(Children[j]);
            }
        }
        return null;
    }

    private double? TargetLeftEdgeAfter(int index)
    {
        for (int j = index + 1; j < Children.Count; j++)
        {
            if (!_glides[Children[j]].Leaving)
            {
                return GetLeft(Children[j]);
            }
        }
        return null;
    }

    private void Hook()
    {
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

    // One layout pass per frame while anything glides; finished leaving children are detached here, outside layout.
    private void OnRendering(object? sender, object e)
    {
        if (_finished.Count > 0)
        {
            foreach (UIElement child in _finished)
            {
                Children.Remove(child);
                _glides.Remove(child);
            }
            _finished.Clear();
        }
        if (_active)
        {
            InvalidateMeasure();
        }
        else
        {
            Unhook();
        }
    }

    // Unloaded (the row closed, or the bar swapped for a placeholder): stop where things were heading.
    private void FinishAll()
    {
        foreach ((UIElement child, Glide g) in _glides.ToList())
        {
            if (g.Leaving)
            {
                Children.Remove(child);
                _glides.Remove(child);
            }
            else
            {
                g.Snap(GetLeft(child), GetSpan(child));
            }
        }
        _finished.Clear();
        _active = false;
    }

    private static double Percent(double value) =>
        double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 100) / 100;

    private static double MinWidthOf(UIElement child) =>
        child is FrameworkElement fe && !double.IsNaN(fe.MinWidth) ? fe.MinWidth : 0;

    private static double Snap(double value, double scale) => Math.Round(value * scale) / scale;

    /// <summary>The shown left/span of one child and where they are heading.</summary>
    private sealed class Glide
    {
        public Glide(double left, double span)
        {
            Left = new Tween(Motion.RowDuration, Motion.RowEase, left);
            Span = new Tween(Motion.RowDuration, Motion.RowEase, span);
        }

        public Tween Left { get; }

        public Tween Span { get; }

        public bool Leaving { get; set; }

        public bool IsActive(long now) => Left.IsActive(now) || Span.IsActive(now);

        public void Retarget(double left, double span, long now, bool animate)
        {
            if (animate)
            {
                Left.Retarget(left, now);
                Span.Retarget(span, now);
            }
            else
            {
                Snap(left, span);
            }
        }

        public void Snap(double left, double span)
        {
            Left.Snap(left);
            Span.Snap(span);
        }

        /// <summary>Shows <paramref name="left"/>/<paramref name="span"/> now and glides to the current targets from there.</summary>
        public void StartFrom(double left, double span, long now)
        {
            double toLeft = Left.Target;
            double toSpan = Span.Target;
            Left.Snap(left);
            Span.Snap(span);
            Left.Retarget(toLeft, now);
            Span.Retarget(toSpan, now);
        }
    }
}
