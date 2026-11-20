using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeleteMan.App.Controls;

/// <summary>
/// Progress bar for a running deletion, shown in place of the row's segments: a red fill of
/// <see cref="Fraction"/> of the width, or animated stripes while <see cref="IsIndeterminate"/> (the total is
/// still being counted), with <see cref="Text"/> drawn over it. The fill glides to each new fraction over
/// 250 ms (the web UI's <c>transition: width 0.25s linear</c>) rather than stepping once per poll.
/// </summary>
public sealed partial class DeletingBar : UserControl
{
    /// <summary>Identifies the <see cref="Fraction"/> dependency property.</summary>
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(DeletingBar), new PropertyMetadata(0.0, (d, _) => ((DeletingBar)d).UpdateFill()));

    /// <summary>Identifies the <see cref="IsIndeterminate"/> dependency property.</summary>
    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(DeletingBar), new PropertyMetadata(false, (d, _) => ((DeletingBar)d).UpdateFill()));

    /// <summary>Identifies the <see cref="Text"/> dependency property.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(DeletingBar),
        new PropertyMetadata(string.Empty, (d, e) => AutomationProperties.SetName((DeletingBar)d, (string)e.NewValue)));

    private static readonly TimeSpan FillDuration = TimeSpan.FromMilliseconds(250);

    private readonly RectangleGeometry _clip = new();
    private readonly Tween _shown = new(FillDuration, Motion.Linear);
    private bool _renderingHooked;

    /// <summary>Creates the bar.</summary>
    public DeletingBar()
    {
        InitializeComponent();
        RootGrid.Clip = _clip;
        Loaded += (_, _) => UpdateFill();
        Unloaded += (_, _) =>
        {
            Unhook();
            _shown.Snap(_shown.Target);
        };
        UpdateFill();
    }

    /// <summary>Done fraction 0..1 (NaN is treated as 0).</summary>
    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    /// <summary>True while the amount to delete is unknown: stripes slide across the whole bar.</summary>
    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    /// <summary>Progress text, e.g. "Deleting… 37 % · 12,345 files · 1.2 GB / 3.2 GB freed · 0:42".</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        UpdateFill();
    }

    /// <summary>Points the fill at the current fraction (gliding there when animations are on) and paints it.</summary>
    private void UpdateFill()
    {
        double f = double.IsNaN(Fraction) ? 0 : Math.Clamp(Fraction, 0, 1);
        long now = Motion.Now;
        if (IsIndeterminate)
        {
            // The first known fraction then grows out of the left edge, as the web UI's fill did.
            _shown.Snap(0);
        }
        else if (Motion.Enabled && IsLoaded)
        {
            _shown.Retarget(f, now);
        }
        else
        {
            _shown.Snap(f);
        }
        Paint(now);
        if (_shown.IsActive(now))
        {
            Hook();
        }
    }

    private void Paint(long now)
    {
        FillRect.Width = IsIndeterminate ? 0 : Math.Round(_shown.ValueAt(now) * RootGrid.ActualWidth);
        Stripes.Visibility = IsIndeterminate ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnRendering(object? sender, object e)
    {
        long now = Motion.Now;
        Paint(now);
        if (!_shown.IsActive(now))
        {
            Unhook();
        }
    }
}
