using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeleteMan.App.Controls;

/// <summary>
/// Hosts one row inside a drive card and lets it be animated open or closed. The caller animates
/// <see cref="FrameworkElement.MaxHeight"/> between 0 and <see cref="OpenHeight"/>; the slot scales the gap above
/// the row and the row's visible height together, clipping the row at the bottom while it is partly open, so a new
/// row grows out of the row above it and a closed row shrinks back into it. This is the web UI's <c>animateRow</c>
/// (height + margin-top, 190 ms); the opacity part is animated by the caller as well.
/// </summary>
/// <remarks>
/// The animated value is a layout property on purpose: rows below move with the real layout every frame, and the
/// ribbon layer's <c>LayoutUpdated</c> hook redraws the ribbons in step (the web UI's <c>pumpSplines</c>). It is a
/// framework property rather than a custom one because a Storyboard started from code can only resolve property
/// names the XAML type system knows. The row keeps its natural height throughout; only the slot shrinks.
/// </remarks>
public sealed partial class RowSlot : Panel
{
    /// <summary>Space between this row and the one above it when fully open (0 for the first row of a card).</summary>
    public double Gap { get; set; }

    /// <summary>Gap plus the row's natural height; known after the first measure, 0 before.</summary>
    public double OpenHeight { get; private set; }

    /// <summary>The hosted row, or null.</summary>
    public UIElement? Content => Children.Count > 0 ? Children[0] : null;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        UIElement? child = Content;
        if (child is null)
        {
            OpenHeight = 0;
            return new Size(0, 0);
        }
        child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        OpenHeight = Gap + child.DesiredSize.Height;
        double width = double.IsInfinity(availableSize.Width) ? child.DesiredSize.Width : availableSize.Width;
        // MaxHeight caps the slot while it animates (the framework clamps the result too; this keeps the two in step).
        double height = double.IsInfinity(MaxHeight) ? OpenHeight : Math.Min(OpenHeight, MaxHeight);
        return new Size(width, height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        UIElement? child = Content;
        if (child is null)
        {
            return finalSize;
        }
        double p = OpenHeight > 0 ? Math.Clamp(finalSize.Height / OpenHeight, 0, 1) : 1;
        child.Arrange(new Rect(0, Gap * p, finalSize.Width, child.DesiredSize.Height));
        // Partly open: hide what hangs below the slot. Fully open: no clip, so focus visuals can overflow.
        Clip = p < 1 ? new RectangleGeometry { Rect = new Rect(0, 0, finalSize.Width, finalSize.Height) } : null;
        return finalSize;
    }
}
