using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace DeleteMan.App.Controls;

/// <summary>How a <see cref="SegmentButton"/> paints its background.</summary>
public enum SegmentAppearance
{
    /// <summary>A folder or file: solid <see cref="SegmentButton.SegmentColor"/>.</summary>
    Plain,

    /// <summary>Not scanned yet: solid colour, pulsing opacity.</summary>
    Pending,

    /// <summary>Could not be read: red 4/8 px hatch, at least 3 px wide.</summary>
    Error,

    /// <summary>The loose files of a folder (structural slate, theme-dependent).</summary>
    Files,

    /// <summary>Everything not listed individually (structural grey).</summary>
    Other,

    /// <summary>The "(small N)" group of tiny children (structural grey).</summary>
    Small,

    /// <summary>Space not attributed to any folder yet: grey 6/12 px hatch with muted text.</summary>
    Hatched,

    /// <summary>The whole-drive bar: track colour with the used fraction filled in the accent colour.</summary>
    Drive,
}

/// <summary>
/// One proportional segment of a bar. A real <see cref="Button"/>, so it is keyboard-focusable, invokable and
/// visible to UI Automation; the consumer handles <see cref="ButtonBase.Click"/> to expand it and sets
/// <c>ToolTipService.ToolTip</c> and <c>AutomationProperties.Name</c>/<c>AutomationId</c>.
/// Segments are square-cornered so neighbours tile edge to edge inside a <see cref="ProportionalPanel"/>.
/// </summary>
public sealed partial class SegmentButton : Button
{
    /// <summary>Below this width the label is hidden (the web UI's container query, max-width 14 px).</summary>
    private const double MinTextWidth = 14;

    /// <summary>Identifies the <see cref="SegmentColor"/> dependency property.</summary>
    public static readonly DependencyProperty SegmentColorProperty = DependencyProperty.Register(
        nameof(SegmentColor), typeof(Color), typeof(SegmentButton),
        new PropertyMetadata(Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6), OnVisualPropertyChanged));

    /// <summary>Identifies the <see cref="Appearance"/> dependency property.</summary>
    public static readonly DependencyProperty AppearanceProperty = DependencyProperty.Register(
        nameof(Appearance), typeof(SegmentAppearance), typeof(SegmentButton),
        new PropertyMetadata(SegmentAppearance.Plain, OnVisualPropertyChanged));

    /// <summary>Identifies the <see cref="Text"/> dependency property.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SegmentButton), new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

    /// <summary>Identifies the <see cref="IsExpanded"/> dependency property.</summary>
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(SegmentButton), new PropertyMetadata(false, OnVisualPropertyChanged));

    /// <summary>Identifies the <see cref="FillFraction"/> dependency property.</summary>
    public static readonly DependencyProperty FillFractionProperty = DependencyProperty.Register(
        nameof(FillFraction), typeof(double), typeof(SegmentButton), new PropertyMetadata(double.NaN, OnVisualPropertyChanged));

    /// <summary>Identifies the <see cref="ProgressFraction"/> dependency property.</summary>
    public static readonly DependencyProperty ProgressFractionProperty = DependencyProperty.Register(
        nameof(ProgressFraction), typeof(double), typeof(SegmentButton), new PropertyMetadata(double.NaN, OnVisualPropertyChanged));

    /// <summary>Identifies the <see cref="IsProgressIndeterminate"/> dependency property.</summary>
    public static readonly DependencyProperty IsProgressIndeterminateProperty = DependencyProperty.Register(
        nameof(IsProgressIndeterminate), typeof(bool), typeof(SegmentButton), new PropertyMetadata(false, OnVisualPropertyChanged));

    /// <summary>Layer order of ContentGrid's children, template parts and lazily created ones alike.</summary>
    private static readonly Dictionary<string, int> ContentOrder = new()
    {
        ["BackgroundRect"] = 0,
        ["HatchFill"] = 1,
        ["FillRect"] = 2,
        ["ProgressRect"] = 3,
        ["Stripes"] = 4,
        ["Separator"] = 5,
        ["TextShadow"] = 6,
        ["TextPart"] = 7,
        ["HoverOverlay"] = 8,
        ["PressedOverlay"] = 9,
    };

    /// <summary>Layer order of RootGrid's children.</summary>
    private static readonly Dictionary<string, int> RootOrder = new()
    {
        ["ContentGrid"] = 0,
        ["ExpandedRing"] = 1,
    };

    private Grid? _root;
    private Grid? _content;
    private Rectangle? _background;
    private Rectangle? _fill;
    private Rectangle? _progress;
    private StripeFill? _hatch;
    private StripeFill? _stripes;
    private Rectangle? _separator;
    private TextBlock? _text;
    private TextBlock? _textShadow;
    private Border? _ring;
    private readonly RectangleGeometry _clip = new();
    private Storyboard? _pulse;

    /// <summary>Creates a segment; its template comes from Controls\SegmentButton.xaml.</summary>
    public SegmentButton()
    {
        DefaultStyleKey = typeof(SegmentButton);
        SizeChanged += (_, _) => UpdateLayoutParts();
        ActualThemeChanged += (_, _) => UpdateVisuals();
        Loaded += (_, _) => UpdatePulse();
        Unloaded += (_, _) => StopPulse();
    }

    /// <summary>Fill colour for <see cref="SegmentAppearance.Plain"/> and <see cref="SegmentAppearance.Pending"/>; also tints the expanded ring.</summary>
    public Color SegmentColor
    {
        get => (Color)GetValue(SegmentColorProperty);
        set => SetValue(SegmentColorProperty, value);
    }

    /// <summary>Background style of the segment.</summary>
    public SegmentAppearance Appearance
    {
        get => (SegmentAppearance)GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    /// <summary>Single-line label (white 12 px, clipped; hidden when the segment is narrower than 14 px).</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>True when this segment is the one opened in the row below: draws a 2 px inner ring.</summary>
    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>Used fraction (0..1) painted in the accent colour over the track; NaN for none. Used by <see cref="SegmentAppearance.Drive"/>.</summary>
    public double FillFraction
    {
        get => (double)GetValue(FillFractionProperty);
        set => SetValue(FillFractionProperty, value);
    }

    /// <summary>Scan progress (0..1) shown as a 35 % white overlay of that width; NaN for none.</summary>
    public double ProgressFraction
    {
        get => (double)GetValue(ProgressFractionProperty);
        set => SetValue(ProgressFractionProperty, value);
    }

    /// <summary>Shows animated diagonal stripes across the whole segment instead of a progress width.</summary>
    public bool IsProgressIndeterminate
    {
        get => (bool)GetValue(IsProgressIndeterminateProperty);
        set => SetValue(IsProgressIndeterminateProperty, value);
    }

    /// <summary>
    /// The used-fraction fill of a drive segment (null before the template is applied). The used row's ribbon hangs
    /// from this element, the web UI's <c>[data-anchor="used"]</c>.
    /// </summary>
    public FrameworkElement? FillElement => _fill;

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        StopPulse();
        base.OnApplyTemplate();

        _root = GetTemplateChild("RootGrid") as Grid;
        _content = GetTemplateChild("ContentGrid") as Grid;
        _background = GetTemplateChild("BackgroundRect") as Rectangle;
        _separator = GetTemplateChild("Separator") as Rectangle;
        _text = GetTemplateChild("TextPart") as TextBlock;
        _textShadow = GetTemplateChild("TextShadow") as TextBlock;
        // The optional parts belong to the previous template's tree; they are created again when needed.
        _fill = null;
        _progress = null;
        _hatch = null;
        _stripes = null;
        _ring = null;

        if (_content is not null)
        {
            // Clip the label and overlays to the segment: a long name in a narrow segment must not spill over.
            _content.Clip = _clip;
        }
        UpdateVisuals();
        UpdatePulse();
    }

    // ---------- Optional parts (created on first use; see SegmentButton.xaml) ----------

    private StripeFill Hatch() => _hatch ??= AddPart(_content, ContentOrder, new StripeFill { Name = "HatchFill" });

    private Rectangle Fill() => _fill ??= AddPart(_content, ContentOrder, new Rectangle { Name = "FillRect", HorizontalAlignment = HorizontalAlignment.Left });

    private Rectangle Progress() => _progress ??= AddPart(_content, ContentOrder, new Rectangle { Name = "ProgressRect", HorizontalAlignment = HorizontalAlignment.Left });

    private StripeFill Stripes() => _stripes ??= AddPart(_content, ContentOrder, new StripeFill { Name = "Stripes", IsAnimated = true });

    private Border Ring() => _ring ??= AddPart(_root, RootOrder, new Border { Name = "ExpandedRing", BorderThickness = new Thickness(2), IsHitTestVisible = false });

    /// <summary>Inserts <paramref name="part"/> into <paramref name="panel"/> at its place in <paramref name="order"/>.</summary>
    private static T AddPart<T>(Panel? panel, Dictionary<string, int> order, T part)
        where T : FrameworkElement
    {
        if (panel is not null)
        {
            int rank = order[part.Name];
            int index = 0;
            while (index < panel.Children.Count
                && panel.Children[index] is FrameworkElement existing
                && order.TryGetValue(existing.Name, out int existingRank)
                && existingRank < rank)
            {
                index++;
            }
            panel.Children.Insert(index, part);
        }
        return part;
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var segment = (SegmentButton)d;
        segment.UpdateVisuals();
        if (e.Property == AppearanceProperty)
        {
            segment.UpdatePulse();
        }
    }

    /// <summary>Applies every appearance-dependent brush and visibility; sizes are handled by <see cref="UpdateLayoutParts"/>.</summary>
    private void UpdateVisuals()
    {
        SegmentAppearance appearance = Appearance;
        // An unreadable folder is often tiny; keep a visible sliver.
        MinWidth = appearance == SegmentAppearance.Error ? 3 : 0;
        if (_background is null)
        {
            return;
        }

        if (appearance == SegmentAppearance.Drive)
        {
            _background.Fill = ThemeLookup.Brush(this, "DriveTrackBrush");
        }
        else
        {
            // Keep the brush when only its colour changes; a new brush per update is a new composition object.
            Color color = BaseColor();
            if (_background.Fill is SolidColorBrush existing)
            {
                existing.Color = color;
            }
            else
            {
                _background.Fill = new SolidColorBrush(color);
            }
        }
        bool hatched = appearance == SegmentAppearance.Hatched;
        bool error = appearance == SegmentAppearance.Error;
        if (hatched || error)
        {
            StripeFill hatch = Hatch();
            hatch.Visibility = Visibility.Visible;
            hatch.StripeWidth = error ? HatchBrushes.ErrorStripeWidth : HatchBrushes.StripeWidth;
            hatch.ColorAKey = error ? "ErrorStripeAColor" : "HatchStripeAColor";
            hatch.ColorBKey = error ? "ErrorStripeBColor" : "HatchStripeBColor";
        }
        else if (_hatch is not null)
        {
            _hatch.Visibility = Visibility.Collapsed;
        }

        bool drive = appearance == SegmentAppearance.Drive;
        bool hasFill = !double.IsNaN(FillFraction);
        if (hasFill)
        {
            Rectangle fill = Fill();
            fill.Visibility = Visibility.Visible;
            fill.Fill = ThemeLookup.Brush(this, "DriveFillBrush");
        }
        else if (_fill is not null)
        {
            _fill.Visibility = Visibility.Collapsed;
        }
        if (_separator is not null)
        {
            _separator.Visibility = drive ? Visibility.Collapsed : Visibility.Visible;
        }
        if (!IsProgressIndeterminate && !double.IsNaN(ProgressFraction))
        {
            Rectangle progress = Progress();
            progress.Visibility = Visibility.Visible;
            progress.Fill = ThemeLookup.Brush(this, "SegmentProgressBrush");
        }
        else if (_progress is not null)
        {
            _progress.Visibility = Visibility.Collapsed;
        }
        if (IsProgressIndeterminate)
        {
            Stripes().Visibility = Visibility.Visible;
        }
        else if (_stripes is not null)
        {
            _stripes.Visibility = Visibility.Collapsed;
        }

        if (_text is not null && _textShadow is not null)
        {
            _text.Text = Text;
            _textShadow.Text = Text;
            _text.Foreground = ThemeLookup.Brush(this, appearance == SegmentAppearance.Hatched ? "HatchedTextBrush" : "SegmentTextBrush");
            // Muted text on the light hatch must not carry a dark shadow.
            _textShadow.Opacity = appearance == SegmentAppearance.Hatched ? 0 : 1;
        }

        if (IsExpanded)
        {
            // A lighter tint of the segment's own colour (CSS color-mix with white); for the drive bar the
            // used fill carries the ring, since the fill covers most of the track.
            Color ringBase = drive && hasFill
                ? ThemeLookup.Color(this, "DriveFillBrush", Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6))
                : BaseColor();
            Border ring = Ring();
            ring.Visibility = Visibility.Visible;
            ring.BorderBrush = new SolidColorBrush(Tint(ringBase, drive ? 0.55 : 0.42));
        }
        else if (_ring is not null)
        {
            _ring.Visibility = Visibility.Collapsed;
        }
        UpdateLayoutParts();
    }

    /// <summary>The solid colour this segment stands for (structural kinds follow the theme).</summary>
    private Color BaseColor() => Appearance switch
    {
        SegmentAppearance.Files => ThemeLookup.Color(this, "StructuralFilesColor", Color.FromArgb(0xFF, 0x64, 0x74, 0x8B)),
        SegmentAppearance.Other => ThemeLookup.Color(this, "StructuralOtherColor", Color.FromArgb(0xFF, 0x4B, 0x55, 0x63)),
        SegmentAppearance.Small => ThemeLookup.Color(this, "StructuralSmallColor", Color.FromArgb(0xFF, 0x52, 0x52, 0x5B)),
        SegmentAppearance.Hatched => ThemeLookup.Color(this, "HatchStripeAColor", Color.FromArgb(0xFF, 0x3A, 0x3F, 0x48)),
        SegmentAppearance.Error => ThemeLookup.Color(this, "ErrorStripeAColor", Color.FromArgb(0xFF, 0x7F, 0x1D, 0x1D)),
        SegmentAppearance.Drive => ThemeLookup.Color(this, "DriveTrackBrush", Color.FromArgb(0xFF, 0x2A, 0x2E, 0x36)),
        _ => SegmentColor,
    };

    /// <summary>Mixes <paramref name="c"/> toward white by <paramref name="amount"/> (0..1).</summary>
    private static Color Tint(Color c, double amount) => Color.FromArgb(
        0xFF,
        (byte)Math.Round(c.R + ((255 - c.R) * amount)),
        (byte)Math.Round(c.G + ((255 - c.G) * amount)),
        (byte)Math.Round(c.B + ((255 - c.B) * amount)));

    /// <summary>Sizes the fraction overlays, the clip and the label to the current segment size.</summary>
    private void UpdateLayoutParts()
    {
        double w = ActualWidth;
        double h = ActualHeight;
        _clip.Rect = new Rect(0, 0, Math.Max(0, w), Math.Max(0, h));

        double fillWidth = Math.Round(Fraction(FillFraction) * w);
        if (_fill is not null)
        {
            _fill.Width = fillWidth;
        }
        if (_progress is not null)
        {
            _progress.Width = Math.Round(Fraction(ProgressFraction) * w);
        }
        if (_ring is not null)
        {
            bool ringFill = Appearance == SegmentAppearance.Drive && !double.IsNaN(FillFraction);
            _ring.HorizontalAlignment = ringFill ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            _ring.Width = ringFill ? Math.Max(fillWidth, 4) : double.NaN;
        }
        if (_text is not null && _textShadow is not null)
        {
            Visibility v = w < MinTextWidth || string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible;
            _text.Visibility = v;
            _textShadow.Visibility = v;
        }
    }

    private static double Fraction(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

    private void UpdatePulse()
    {
        if (_content is null)
        {
            return;
        }
        if (Appearance == SegmentAppearance.Pending && IsLoaded && Motion.Enabled)
        {
            _pulse ??= Motion.Pulse(_content, 0.65, TimeSpan.FromSeconds(1.6));
            _pulse.Begin();
        }
        else
        {
            StopPulse();
        }
    }

    private void StopPulse()
    {
        if (_pulse is not null)
        {
            _pulse.Stop();
            _pulse = null;
        }
        if (_content is not null)
        {
            _content.Opacity = 1;
        }
    }
}
