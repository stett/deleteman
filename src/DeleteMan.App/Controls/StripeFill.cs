using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DeleteMan.App.Controls;

/// <summary>
/// Fills its bounds with 135° diagonal stripes in two theme colours (see <see cref="HatchBrushes"/> for why this
/// is geometry rather than a gradient brush). With <see cref="IsAnimated"/> the stripes slide right forever,
/// the "busy, amount unknown" progress look: the stripe layer is one period wider than the control and is
/// translated by exactly one period, which keeps the animation on the compositor thread.
/// </summary>
public sealed partial class StripeFill : Canvas
{
    /// <summary>Identifies the <see cref="ColorAKey"/> dependency property.</summary>
    public static readonly DependencyProperty ColorAKeyProperty = DependencyProperty.Register(
        nameof(ColorAKey), typeof(string), typeof(StripeFill),
        new PropertyMetadata("IndeterminateStripeAColor", (d, _) => ((StripeFill)d).ApplyColors()));

    /// <summary>Identifies the <see cref="ColorBKey"/> dependency property.</summary>
    public static readonly DependencyProperty ColorBKeyProperty = DependencyProperty.Register(
        nameof(ColorBKey), typeof(string), typeof(StripeFill),
        new PropertyMetadata("IndeterminateStripeBColor", (d, _) => ((StripeFill)d).ApplyColors()));

    /// <summary>Identifies the <see cref="StripeWidth"/> dependency property.</summary>
    public static readonly DependencyProperty StripeWidthProperty = DependencyProperty.Register(
        nameof(StripeWidth), typeof(double), typeof(StripeFill),
        new PropertyMetadata(HatchBrushes.ProgressStripeWidth, (d, _) => ((StripeFill)d).Rebuild()));

    /// <summary>Identifies the <see cref="IsAnimated"/> dependency property.</summary>
    public static readonly DependencyProperty IsAnimatedProperty = DependencyProperty.Register(
        nameof(IsAnimated), typeof(bool), typeof(StripeFill),
        new PropertyMetadata(false, (d, _) => ((StripeFill)d).OnAnimatedChanged()));

    private readonly Path _bandsA = new() { IsHitTestVisible = false };
    private readonly Path _bandsB = new() { IsHitTestVisible = false };
    private readonly TranslateTransform _shift = new();
    private readonly RectangleGeometry _clip = new();
    private Storyboard? _storyboard;
    private Size _builtFor;

    /// <summary>Creates the fill; colours default to the translucent white progress stripes.</summary>
    public StripeFill()
    {
        IsHitTestVisible = false;
        _bandsA.RenderTransform = _shift;
        _bandsB.RenderTransform = _shift;
        Children.Add(_bandsA);
        Children.Add(_bandsB);
        Clip = _clip;
        SizeChanged += (_, _) => Rebuild();
        Loaded += (_, _) =>
        {
            ApplyColors();
            UpdateAnimation();
        };
        Unloaded += (_, _) => StopAnimation();
        ActualThemeChanged += (_, _) => ApplyColors();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateAnimation());
    }

    /// <summary>Theme resource key of the first band colour (resolved with <see cref="ThemeLookup"/>).</summary>
    public string ColorAKey
    {
        get => (string)GetValue(ColorAKeyProperty);
        set => SetValue(ColorAKeyProperty, value);
    }

    /// <summary>Theme resource key of the second band colour.</summary>
    public string ColorBKey
    {
        get => (string)GetValue(ColorBKeyProperty);
        set => SetValue(ColorBKeyProperty, value);
    }

    /// <summary>Band width across the stripes in DIPs (6 hatch, 4 error, 10 progress).</summary>
    public double StripeWidth
    {
        get => (double)GetValue(StripeWidthProperty);
        set => SetValue(StripeWidthProperty, value);
    }

    /// <summary>Slides the stripes continuously (only while visible, loaded and animations are enabled).</summary>
    public bool IsAnimated
    {
        get => (bool)GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    private void ApplyColors()
    {
        Color a = ThemeLookup.Color(this, ColorAKey, Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF));
        Color b = ThemeLookup.Color(this, ColorBKey, Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        _bandsA.Fill = new SolidColorBrush(a);
        _bandsB.Fill = new SolidColorBrush(b);
    }

    private void OnAnimatedChanged()
    {
        _builtFor = default;
        Rebuild();
        UpdateAnimation();
    }

    private void Rebuild()
    {
        var size = new Size(ActualWidth, ActualHeight);
        _clip.Rect = new Rect(0, 0, size.Width, size.Height);
        if (size == _builtFor || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }
        _builtFor = size;
        // An animated layer needs one spare period on the left that slides into view.
        double left = IsAnimated ? -HatchBrushes.PeriodX(StripeWidth) : 0;
        _bandsA.Data = HatchBrushes.Bands(left, size.Width, size.Height, StripeWidth, 0);
        _bandsB.Data = HatchBrushes.Bands(left, size.Width, size.Height, StripeWidth, 0.5);
    }

    private void UpdateAnimation()
    {
        if (IsAnimated && Visibility == Visibility.Visible && IsLoaded && Motion.Enabled)
        {
            // CSS: the stripe tile moves one period every 0.8 s.
            _storyboard ??= Motion.Slide(_shift, HatchBrushes.PeriodX(StripeWidth), TimeSpan.FromSeconds(0.8));
            _storyboard.Begin();
        }
        else
        {
            StopAnimation();
        }
    }

    private void StopAnimation()
    {
        _storyboard?.Stop();
        _shift.X = 0;
    }
}
