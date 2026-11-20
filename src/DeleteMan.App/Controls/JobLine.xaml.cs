using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DeleteMan.App.Controls;

/// <summary>Status glyph of a <see cref="JobLine"/>.</summary>
public enum JobGlyph
{
    /// <summary>Waiting for an earlier job (U+E712, "more").</summary>
    Queued,

    /// <summary>Deleting now (a small ProgressRing).</summary>
    Running,

    /// <summary>Finished cleanly (U+E73E check, success colour).</summary>
    Done,

    /// <summary>Finished but some items could not be deleted (U+E7BA warning, critical colour).</summary>
    DoneWithErrors,

    /// <summary>Stopped by the user (U+E71A stop).</summary>
    Cancelled,

    /// <summary>Could not run at all (U+E711 cross, critical colour).</summary>
    Failed,
}

/// <summary>
/// One line of the jobs banner. Shows a red Cancel button while <see cref="IsActive"/>, otherwise a ✕ dismiss
/// button; a Details button appears when <see cref="HasErrors"/>. Inner buttons get automation ids
/// <c>{AutomationKey}:details</c>, <c>:cancel</c> and <c>:dismiss</c>.
/// </summary>
public sealed partial class JobLine : UserControl
{
    /// <summary>Identifies the <see cref="Glyph"/> dependency property.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(JobGlyph), typeof(JobLine), new PropertyMetadata(JobGlyph.Queued, OnStateChanged));

    /// <summary>Identifies the <see cref="Path"/> dependency property.</summary>
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(JobLine), new PropertyMetadata(string.Empty, OnStateChanged));

    /// <summary>Identifies the <see cref="StatusText"/> dependency property.</summary>
    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(JobLine), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="IsResumed"/> dependency property.</summary>
    public static readonly DependencyProperty IsResumedProperty = DependencyProperty.Register(
        nameof(IsResumed), typeof(bool), typeof(JobLine), new PropertyMetadata(false, OnStateChanged));

    /// <summary>Identifies the <see cref="HasErrors"/> dependency property.</summary>
    public static readonly DependencyProperty HasErrorsProperty = DependencyProperty.Register(
        nameof(HasErrors), typeof(bool), typeof(JobLine), new PropertyMetadata(false, OnStateChanged));

    /// <summary>Identifies the <see cref="IsActive"/> dependency property.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(JobLine), new PropertyMetadata(false, OnStateChanged));

    /// <summary>Identifies the <see cref="AutomationKey"/> dependency property.</summary>
    public static readonly DependencyProperty AutomationKeyProperty = DependencyProperty.Register(
        nameof(AutomationKey), typeof(string), typeof(JobLine), new PropertyMetadata("job", OnStateChanged));

    /// <summary>Creates an empty line.</summary>
    public JobLine()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateState();
        Unloaded += (_, _) => RunningRing.IsActive = false;
        UpdateState();
    }

    /// <summary>Raised by the Details button.</summary>
    public event TypedEventHandler<JobLine, RoutedEventArgs>? DetailsClicked;

    /// <summary>Raised by the Cancel button.</summary>
    public event TypedEventHandler<JobLine, RoutedEventArgs>? CancelClicked;

    /// <summary>Raised by the dismiss (✕) button.</summary>
    public event TypedEventHandler<JobLine, RoutedEventArgs>? DismissClicked;

    /// <summary>Status glyph.</summary>
    public JobGlyph Glyph
    {
        get => (JobGlyph)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>The path being deleted (monospace, ellipsis, at most 45 % of the line).</summary>
    public string Path
    {
        get => (string)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    /// <summary>Status/progress text (secondary colour, tabular numerals).</summary>
    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    /// <summary>Shows the "resumed" pill: the job was restarted after the app was closed mid-deletion.</summary>
    public bool IsResumed
    {
        get => (bool)GetValue(IsResumedProperty);
        set => SetValue(IsResumedProperty, value);
    }

    /// <summary>Shows the Details button.</summary>
    public bool HasErrors
    {
        get => (bool)GetValue(HasErrorsProperty);
        set => SetValue(HasErrorsProperty, value);
    }

    /// <summary>True while the job is queued or running: Cancel replaces dismiss.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Prefix for the inner buttons' automation ids, e.g. "job:3" gives "job:3:details".</summary>
    public string AutomationKey
    {
        get => (string)GetValue(AutomationKeyProperty);
        set => SetValue(AutomationKeyProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((JobLine)d).UpdateState();

    private void UpdateState()
    {
        JobGlyph glyph = Glyph;
        (FontIcon? icon, string text) = glyph switch
        {
            JobGlyph.Queued => (NeutralIcon, ""),
            JobGlyph.Done => (SuccessIcon, ""),
            JobGlyph.DoneWithErrors => (CriticalIcon, ""),
            JobGlyph.Cancelled => (NeutralIcon, ""),
            JobGlyph.Failed => (CriticalIcon, ""),
            _ => ((FontIcon?)null, string.Empty),
        };
        foreach (FontIcon candidate in (ReadOnlySpan<FontIcon>)[NeutralIcon, SuccessIcon, CriticalIcon])
        {
            candidate.Visibility = ReferenceEquals(candidate, icon) ? Visibility.Visible : Visibility.Collapsed;
        }
        if (icon is not null)
        {
            icon.Glyph = text;
        }
        bool running = glyph == JobGlyph.Running;
        RunningRing.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        // Only spin while shown: an inactive ring costs nothing.
        RunningRing.IsActive = running && IsLoaded;

        ResumedPill.Visibility = IsResumed ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Visibility = HasErrors ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = IsActive ? Visibility.Visible : Visibility.Collapsed;
        DismissButton.Visibility = IsActive ? Visibility.Collapsed : Visibility.Visible;

        string key = AutomationKey;
        AutomationProperties.SetAutomationId(DetailsButton, key + ":details");
        AutomationProperties.SetAutomationId(CancelButton, key + ":cancel");
        AutomationProperties.SetAutomationId(DismissButton, key + ":dismiss");
        AutomationProperties.SetName(this, Path);
        ToolTipService.SetToolTip(PathText, string.IsNullOrEmpty(Path) ? null : Path);
    }

    // The web UI caps the path at 45 % of the line so the status text always keeps room.
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => PathText.MaxWidth = Math.Max(0, e.NewSize.Width * 0.45);

    private void OnDetailsClick(object sender, RoutedEventArgs e) => DetailsClicked?.Invoke(this, e);

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelClicked?.Invoke(this, e);

    private void OnDismissClick(object sender, RoutedEventArgs e) => DismissClicked?.Invoke(this, e);
}
