using DeleteMan.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DeleteMan.App.Controls;

/// <summary>
/// One explorer row: label (name + sub line), a bar slot, the size, and the row controls: two joined pairs of
/// 28 px buttons, [Recycle Bin | permanent delete] and [pin | close], in fixed columns so they line up in every
/// row. Pin and close are on every row and disabled where they do not apply; Recycle Bin and delete are shown
/// only where they do; <see cref="ShowCancel"/> replaces them all with one red Cancel button.
/// </summary>
public sealed partial class RowControl : UserControl
{
    /// <summary>Identifies the <see cref="Label"/> dependency property.</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RowControl), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="SubLabel"/> dependency property.</summary>
    public static readonly DependencyProperty SubLabelProperty = DependencyProperty.Register(
        nameof(SubLabel), typeof(string), typeof(RowControl), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="SizeText"/> dependency property.</summary>
    public static readonly DependencyProperty SizeTextProperty = DependencyProperty.Register(
        nameof(SizeText), typeof(string), typeof(RowControl), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="BarContent"/> dependency property.</summary>
    public static readonly DependencyProperty BarContentProperty = DependencyProperty.Register(
        nameof(BarContent), typeof(object), typeof(RowControl), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="EdgeColor"/> dependency property.</summary>
    public static readonly DependencyProperty EdgeColorProperty = DependencyProperty.Register(
        nameof(EdgeColor), typeof(Color), typeof(RowControl),
        new PropertyMetadata(Colors.Transparent, (d, _) => ((RowControl)d).UpdateEdge()));

    /// <summary>Identifies the <see cref="LabelToolTip"/> dependency property.</summary>
    public static readonly DependencyProperty LabelToolTipProperty = DependencyProperty.Register(
        nameof(LabelToolTip), typeof(object), typeof(RowControl),
        new PropertyMetadata(null, (d, e) => ToolTipService.SetToolTip(((RowControl)d).LabelCell, e.NewValue)));

    /// <summary>Identifies the <see cref="ShowRecycle"/> dependency property.</summary>
    public static readonly DependencyProperty ShowRecycleProperty = RegisterFlag(nameof(ShowRecycle));

    /// <summary>Identifies the <see cref="ShowDelete"/> dependency property.</summary>
    public static readonly DependencyProperty ShowDeleteProperty = RegisterFlag(nameof(ShowDelete));

    /// <summary>Identifies the <see cref="CloseEnabled"/> dependency property.</summary>
    public static readonly DependencyProperty CloseEnabledProperty = RegisterFlag(nameof(CloseEnabled));

    /// <summary>Identifies the <see cref="ShowCancel"/> dependency property.</summary>
    public static readonly DependencyProperty ShowCancelProperty = RegisterFlag(nameof(ShowCancel));

    private PinState _pin = PinState.None;
    private bool _pinStartsScan;

    /// <summary>Creates an empty row.</summary>
    public RowControl()
    {
        InitializeComponent();
        UpdateEdge();
        UpdatePin();
        UpdateControls();
    }

    /// <summary>Raised by the pin toggle.</summary>
    public event TypedEventHandler<RowControl, RoutedEventArgs>? PinClicked;

    /// <summary>
    /// The pin toggle (column 2): disabled for <see cref="PinState.None"/>, neutral when off, accent-filled when
    /// on. A plain property rather than a dependency property: the enum lives in the view-model assembly, and
    /// boxing it into WinRT property metadata buys nothing here.
    /// </summary>
    public PinState Pin
    {
        get => _pin;
        set
        {
            if (_pin != value)
            {
                _pin = value;
                UpdatePin();
                UpdateControls();
            }
        }
    }

    /// <summary>
    /// True on a collapsed drive row, whose pin also starts the scan; only the tooltip of the "off" state differs.
    /// An open drive row shows the root row's pin and gets the ordinary tooltip.
    /// </summary>
    public bool PinStartsScan
    {
        get => _pinStartsScan;
        set
        {
            if (_pinStartsScan != value)
            {
                _pinStartsScan = value;
                UpdatePin();
            }
        }
    }

    /// <summary>Raised by the Recycle Bin button.</summary>
    public event TypedEventHandler<RowControl, RoutedEventArgs>? RecycleClicked;

    /// <summary>Raised by the permanent delete button.</summary>
    public event TypedEventHandler<RowControl, RoutedEventArgs>? DeleteClicked;

    /// <summary>Raised by the close button.</summary>
    public event TypedEventHandler<RowControl, RoutedEventArgs>? CloseClicked;

    /// <summary>Raised by the red Cancel button of a deleting row.</summary>
    public event TypedEventHandler<RowControl, RoutedEventArgs>? CancelClicked;

    /// <summary>Row name (bold, single line, ellipsis).</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Muted 11 px line under the name (file system, counts, scan state).</summary>
    public string SubLabel
    {
        get => (string)GetValue(SubLabelProperty);
        set => SetValue(SubLabelProperty, value);
    }

    /// <summary>Right-aligned size text, drawn with tabular numerals so columns of sizes line up.</summary>
    public string SizeText
    {
        get => (string)GetValue(SizeTextProperty);
        set => SetValue(SizeTextProperty, value);
    }

    /// <summary>What fills the 26 px bar slot: a <see cref="ProportionalPanel"/> of segments, a placeholder, a <see cref="DeletingBar"/>.</summary>
    public object? BarContent
    {
        get => GetValue(BarContentProperty);
        set => SetValue(BarContentProperty, value);
    }

    /// <summary>Colour of the 3 px left edge (the opening segment's colour); transparent for drive rows.</summary>
    public Color EdgeColor
    {
        get => (Color)GetValue(EdgeColorProperty);
        set => SetValue(EdgeColorProperty, value);
    }

    /// <summary>Tooltip for the label cell (the full name/path when it is truncated).</summary>
    public object? LabelToolTip
    {
        get => GetValue(LabelToolTipProperty);
        set => SetValue(LabelToolTipProperty, value);
    }

    /// <summary>Shows the Recycle Bin button (column 0).</summary>
    public bool ShowRecycle
    {
        get => (bool)GetValue(ShowRecycleProperty);
        set => SetValue(ShowRecycleProperty, value);
    }

    /// <summary>Shows the permanent delete button (column 1).</summary>
    public bool ShowDelete
    {
        get => (bool)GetValue(ShowDeleteProperty);
        set => SetValue(ShowDeleteProperty, value);
    }

    /// <summary>Enables the close button (column 3); it is shown on every row, disabled on a collapsed drive.</summary>
    public bool CloseEnabled
    {
        get => (bool)GetValue(CloseEnabledProperty);
        set => SetValue(CloseEnabledProperty, value);
    }

    /// <summary>Replaces the buttons with a single red Cancel button (a running deletion).</summary>
    public bool ShowCancel
    {
        get => (bool)GetValue(ShowCancelProperty);
        set => SetValue(ShowCancelProperty, value);
    }

    /// <summary>The bar slot element: the ribbon target of this row and the ribbon source height of its anchors.</summary>
    public FrameworkElement Bar => BarBorder;

    private static DependencyProperty RegisterFlag(string name) => DependencyProperty.Register(
        name, typeof(bool), typeof(RowControl), new PropertyMetadata(false, (d, _) => ((RowControl)d).UpdateControls()));

    private void UpdateEdge() => EdgeBorder.BorderBrush = new SolidColorBrush(EdgeColor);

    private void UpdateControls()
    {
        bool cancel = ShowCancel;
        Visibility shown = cancel ? Visibility.Collapsed : Visibility.Visible;
        // Pin and close are on every row, for a steady layout; they are disabled where they cannot act.
        PinButton.Visibility = shown;
        PinButton.IsEnabled = Pin != PinState.None;
        CloseButton.Visibility = shown;
        CloseButton.IsEnabled = CloseEnabled;
        RecycleButton.Visibility = !cancel && ShowRecycle ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = !cancel && ShowDelete ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
    }

    // The pin is drawn as a double chevron down ("expand all"; IconButton.Shape in RowControl.xaml), so its states
    // differ by style, tooltip and automation name only; None is the disabled button of a row with no folders below.
    private void UpdatePin()
    {
        (string style, string tip, string name) = Pin switch
        {
            PinState.On => ("PinnedIconButtonStyle",
                "Pinned: the largest folder or file is kept open at every level below. Click to unpin.", "Unpin (pinned)"),
            PinState.None => ("IconButtonStyle", "Nothing to keep open below this row", "Pin (not available)"),
            _ => ("IconButtonStyle",
                PinStartsScan
                    ? "Pin: scan this drive and keep the largest folder or file open at every level (one pin per drive)"
                    : "Pin: keep the largest folder or file open at every level below this row (one pin per drive)", "Pin"),
        };
        PinButton.Style = AppResources.Get<Style>(style);
        ToolTipService.SetToolTip(PinButton, tip);
        AutomationProperties.SetName(PinButton, name);
        AutomationProperties.SetHelpText(PinButton, tip);
    }

    private void OnPinClick(object sender, RoutedEventArgs e) => PinClicked?.Invoke(this, e);

    private void OnRecycleClick(object sender, RoutedEventArgs e) => RecycleClicked?.Invoke(this, e);

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteClicked?.Invoke(this, e);

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseClicked?.Invoke(this, e);

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelClicked?.Invoke(this, e);
}
