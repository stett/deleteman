using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DeleteMan.App.Controls;

/// <summary>Icons drawn as strokes instead of a font glyph (Segoe Fluent Icons has no vertical double chevron).</summary>
public enum IconShape
{
    /// <summary>Show <see cref="IconButton.Glyph"/>.</summary>
    None,

    /// <summary>A single chevron pointing up: "contract this" (the row close button).</summary>
    ChevronUp,

    /// <summary>Two chevrons pointing down: "expand all" (the row pin).</summary>
    DoubleChevronDown,
}

/// <summary>
/// A compact square button showing one Segoe Fluent Icons glyph or a drawn <see cref="Shape"/>: the row controls
/// (28×26) and the job dismiss button (22×22). <see cref="HoverBackground"/>/<see cref="HoverForeground"/> give
/// destructive buttons their warning colour only while the pointer is over them, like the web UI's
/// <c>.ctl.danger:hover</c>.
/// </summary>
public sealed partial class IconButton : Button
{
    /// <summary>Identifies the <see cref="Glyph"/> dependency property.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(IconButton), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="GlyphSize"/> dependency property.</summary>
    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(IconButton), new PropertyMetadata(14.0));

    /// <summary>Identifies the <see cref="Shape"/> dependency property.</summary>
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(IconShape), typeof(IconButton),
        new PropertyMetadata(IconShape.None, (d, _) => ((IconButton)d).UpdateShape()));

    /// <summary>Identifies the <see cref="HoverBackground"/> dependency property.</summary>
    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.Register(
        nameof(HoverBackground), typeof(Brush), typeof(IconButton), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="HoverForeground"/> dependency property.</summary>
    public static readonly DependencyProperty HoverForegroundProperty = DependencyProperty.Register(
        nameof(HoverForeground), typeof(Brush), typeof(IconButton),
        new PropertyMetadata(null, (d, _) => ((IconButton)d).UpdateHoverIcon()));

    private FontIcon? _icon;
    private FontIcon? _hoverIcon;
    private Path? _shape;
    private Path? _hoverShape;

    /// <summary>Creates the button with its default style (Styles\Controls.xaml).</summary>
    public IconButton()
    {
        DefaultStyleKey = typeof(IconButton);
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => UpdateHoverIcon());
    }

    /// <summary>Segoe Fluent Icons glyph to show, for example U+E711 (Cancel, used to dismiss a job line).</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Glyph font size in DIPs (14 for row controls, 11 for the mini button).</summary>
    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    /// <summary>A drawn icon shown in place of the glyph; <see cref="IconShape.None"/> shows <see cref="Glyph"/>.</summary>
    public IconShape Shape
    {
        get => (IconShape)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    /// <summary>Background painted while the pointer is over the button; null keeps the neutral hover.</summary>
    public Brush? HoverBackground
    {
        get => (Brush?)GetValue(HoverBackgroundProperty);
        set => SetValue(HoverBackgroundProperty, value);
    }

    /// <summary>Icon colour while the pointer is over the button; null keeps <see cref="Control.Foreground"/>.</summary>
    public Brush? HoverForeground
    {
        get => (Brush?)GetValue(HoverForegroundProperty);
        set => SetValue(HoverForegroundProperty, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _icon = GetTemplateChild("Icon") as FontIcon;
        _hoverIcon = GetTemplateChild("HoverIcon") as FontIcon;
        _shape = GetTemplateChild("IconShape") as Path;
        _hoverShape = GetTemplateChild("HoverIconShape") as Path;
        UpdateHoverIcon();
        UpdateShape();
    }

    // The template cross-fades two copies of the icon on hover; the hover copy falls back to the normal foreground
    // so a button without a HoverForeground still shows its icon.
    private void UpdateHoverIcon()
    {
        Brush hover = HoverForeground ?? Foreground;
        if (_hoverIcon is not null)
        {
            _hoverIcon.Foreground = hover;
        }
        if (_hoverShape is not null)
        {
            _hoverShape.Stroke = hover;
        }
    }

    // Shows either the glyph pair or the drawn pair. Each Path gets its own geometry: a Geometry cannot be shared
    // between two shapes.
    private void UpdateShape()
    {
        IconShape shape = Shape;
        bool drawn = shape != IconShape.None;
        if (_icon is not null)
        {
            _icon.Visibility = drawn ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_hoverIcon is not null)
        {
            _hoverIcon.Visibility = drawn ? Visibility.Collapsed : Visibility.Visible;
        }
        Apply(_shape);
        Apply(_hoverShape);

        void Apply(Path? path)
        {
            if (path is null)
            {
                return;
            }
            path.Visibility = drawn ? Visibility.Visible : Visibility.Collapsed;
            if (drawn)
            {
                path.Data = BuildGeometry(shape);
            }
        }
    }

    /// <summary>The strokes of <paramref name="shape"/> in the 12 × 12 box the template gives the Path.</summary>
    private static PathGeometry BuildGeometry(IconShape shape)
    {
        var geometry = new PathGeometry();
        switch (shape)
        {
            case IconShape.ChevronUp:
                geometry.Figures.Add(Chevron(top: 3.75, pointsUp: true));
                break;
            case IconShape.DoubleChevronDown:
                geometry.Figures.Add(Chevron(top: 1.5, pointsUp: false));
                geometry.Figures.Add(Chevron(top: 6, pointsUp: false));
                break;
        }
        return geometry;
    }

    /// <summary>A 10 × 4.5 open chevron whose box starts <paramref name="top"/> pixels down (round caps come from the template).</summary>
    private static PathFigure Chevron(double top, bool pointsUp)
    {
        double baseY = pointsUp ? top + 4.5 : top;
        double apexY = pointsUp ? top : top + 4.5;
        var figure = new PathFigure { StartPoint = new Point(1, baseY), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new LineSegment { Point = new Point(6, apexY) });
        figure.Segments.Add(new LineSegment { Point = new Point(11, baseY) });
        return figure;
    }
}
