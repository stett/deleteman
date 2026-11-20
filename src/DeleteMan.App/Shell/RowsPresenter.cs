using System.Diagnostics;
using DeleteMan.App.Controls;
using DeleteMan.Engine;
using DeleteMan.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using RowButton = DeleteMan.ViewModels.RowControl;
using RowControl = DeleteMan.App.Controls.RowControl;

namespace DeleteMan.App.Shell;

/// <summary>
/// Renders the row views into one rounded card per drive (port of render.ts <c>renderRows</c>/<c>fillRow</c>/
/// <c>drawSplines</c>). Rows are keyed by position (<see cref="RowView.Key"/>) and updated in place: a row is only
/// touched when its view changed, and its segments are reused by key, so a segment under the pointer or with
/// keyboard focus survives the refresh every poll brings while a scan runs. Segment proportions are only ever
/// set as targets: the <see cref="ProportionalPanel"/> glides each segment to its new place.
/// </summary>
internal sealed class RowsPresenter
{
    /// <summary>Vertical gap between the rows of a card (the web UI's row margin), animated with the row.</summary>
    private const double RowGap = 26;

    /// <summary>Ribbon colour for a row without an edge colour (drawSplines' fallback).</summary>
    private static readonly Color FallbackEdge = Color.FromArgb(0xFF, 0x88, 0x88, 0x88);

    private readonly Panel _cardsHost;
    private readonly Dictionary<string, Card> _cards = [];
    private readonly Dictionary<string, RowEntry> _rows = [];
    private bool _busy;
    private static int _tooltipsBuilt;

    /// <summary>
    /// New rows per render. Building a row's segments is the expensive part of a render, and several rows at
    /// once (a pin opening every level of an already scanned folder) stalled the frame their open animations
    /// should have started in. The rest wait for the next frame (<see cref="_deferred"/>).
    /// </summary>
    private const int MaxNewRowsPerRender = 1;

    private IReadOnlyList<RowView>? _deferred;
    private bool _deferredBusy;
    private bool _deferredHooked;

    /// <summary>Renders into <paramref name="cardsHost"/>, a vertical panel that receives one card per drive.</summary>
    public RowsPresenter(Panel cardsHost)
    {
        _cardsHost = cardsHost;
    }

    /// <summary>A segment was clicked or invoked (only raised for expandable segments).</summary>
    public event Action<RowView, SegView>? SegmentInvoked;

    /// <summary>The row's ✕ was clicked.</summary>
    public event Action<RowView>? CloseInvoked;

    /// <summary>The row's pin toggle was clicked (the drive row's pin stands for its root row).</summary>
    public event Action<RowView>? PinInvoked;

    /// <summary>Recycle Bin / permanent delete of a row: its folder, or for a file row the one file it shows.</summary>
    public event Action<RowView, DeleteMode>? DeleteInvoked;

    /// <summary>The red Cancel of a deleting row; the argument is the job id.</summary>
    public event Action<string>? CancelInvoked;

    /// <summary>Reconciles the cards and rows with <paramref name="views"/> (display order, grouped by drive).</summary>
    /// <param name="views">Output of <see cref="Rows.Build"/>.</param>
    /// <param name="busy">A deletion runs: every row except deleting ones is dimmed and disabled.</param>
    public void Render(IReadOnlyList<RowView> views, bool busy)
    {
        _busy = busy;
        _deferred = null;
        int created = 0;
        var groups = new List<(string Letter, List<RowView> Views)>();
        foreach (RowView v in views)
        {
            if (groups.Count > 0 && groups[^1].Letter == v.Letter)
            {
                groups[^1].Views.Add(v);
            }
            else
            {
                groups.Add((v.Letter, [v]));
            }
        }

        var seenRows = new HashSet<string>();
        var seenCards = new HashSet<string>();
        for (int g = 0; g < groups.Count; g++)
        {
            (string letter, List<RowView> rows) = groups[g];
            seenCards.Add(letter);
            if (!_cards.TryGetValue(letter, out Card? card))
            {
                card = new Card(letter);
                _cards[letter] = card;
            }
            PlaceAt(_cardsHost.Children, card.Root, g);

            int position = 0;
            foreach (RowView v in rows)
            {
                bool fresh = !_rows.TryGetValue(v.Key, out RowEntry? entry);
                if (fresh && created >= MaxNewRowsPerRender)
                {
                    // Rows are keyed by position, so a new row is always below every existing one of its card:
                    // this row and the rest of the card wait for the next frame.
                    _deferred = views;
                    _deferredBusy = busy;
                    break;
                }
                seenRows.Add(v.Key);
                if (entry is null)
                {
                    entry = CreateRow(card, v);
                    _rows[v.Key] = entry;
                    created++;
                }
                if (fresh || !entry.View.Equals(v))
                {
                    long t0 = Perf.Enabled ? Stopwatch.GetTimestamp() : 0;
                    int tipsBefore = _tooltipsBuilt;
                    Update(entry, v);
                    if (Perf.Enabled)
                    {
                        Perf.Note($"row {v.Key} {(fresh ? "created" : "updated")}: {v.Segs.Count} segments, "
                            + $"{_tooltipsBuilt - tipsBefore} tooltips, {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} ms");
                    }
                }
                ApplyBusy(entry);
                // Leaving rows keep their place until they have shrunk away; skip over them when placing live rows.
                position = SkipLeaving(card.Rows.Children, position);
                PlaceAt(card.Rows.Children, entry.Slot, position);
                position++;
                if (fresh)
                {
                    AnimateOpen(entry.Slot);
                }
            }
            card.Views = rows;
        }

        foreach ((string key, RowEntry entry) in _rows.ToList())
        {
            if (!seenRows.Contains(key))
            {
                _rows.Remove(key);
                AnimateClosedAndRemove(entry);
            }
        }
        foreach ((string letter, Card card) in _cards.ToList())
        {
            if (!seenCards.Contains(letter))
            {
                _cards.Remove(letter);
                _cardsHost.Children.Remove(card.Root);
            }
        }

        foreach (Card card in _cards.Values)
        {
            card.Ribbons.Opacity = busy ? 0.45 : 1;
            UpdateRibbons(card);
        }

        if (_deferred is not null && !_deferredHooked)
        {
            _deferredHooked = true;
            CompositionTarget.Rendering += OnRenderingDeferred;
        }
    }

    private void OnRenderingDeferred(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnRenderingDeferred;
        _deferredHooked = false;
        if (_deferred is { } views)
        {
            // A newer render (the next poll) may have replaced the views in the meantime; Render cleared the field.
            Render(views, _deferredBusy);
        }
    }

    // ---------- Rows ----------

    private RowEntry CreateRow(Card card, RowView v)
    {
        var control = new RowControl();
        // The drive row heads its card, so it has no gap above it; every other row opens with one.
        var slot = new RowSlot { Gap = v.Kind == RowKind.Drive ? 0 : RowGap };
        slot.Children.Add(control);
        var entry = new RowEntry(card, slot, control, v);
        control.CloseClicked += (_, _) => CloseInvoked?.Invoke(entry.View);
        control.PinClicked += (_, _) => PinInvoked?.Invoke(entry.View);
        control.RecycleClicked += (_, _) => DeleteInvoked?.Invoke(entry.View, DeleteMode.Recycle);
        control.DeleteClicked += (_, _) => DeleteInvoked?.Invoke(entry.View, DeleteMode.Permanent);
        control.CancelClicked += (_, _) =>
        {
            if (entry.View.Deleting is { } d)
            {
                CancelInvoked?.Invoke(d.JobId);
            }
        };
        return entry;
    }

    private void Update(RowEntry entry, RowView v)
    {
        entry.View = v;
        RowControl c = entry.Control;
        c.Label = v.Label;
        c.SubLabel = v.Sub;
        c.SizeText = v.Size;
        c.EdgeColor = v.Edge is { } edge ? edge.ToColor() : Colors.Transparent;
        c.LabelToolTip = string.IsNullOrEmpty(v.Title) ? null : v.Title;
        c.ShowCancel = v.Deleting is not null;
        c.ShowRecycle = v.Controls.Contains(RowButton.Recycle);
        c.ShowDelete = v.Controls.Contains(RowButton.Delete);
        c.CloseEnabled = v.Controls.Contains(RowButton.Close);
        c.PinStartsScan = v.Kind == RowKind.Drive && v.Expanded is null;
        c.Pin = v.Pin;
        AutomationProperties.SetAutomationId(c, $"row:{v.Key}");
        AutomationProperties.SetName(c, string.IsNullOrEmpty(v.Title) ? v.Label : v.Title);

        if (v.Deleting is { } deleting)
        {
            entry.DeletingBar ??= new DeletingBar();
            entry.DeletingBar.IsIndeterminate = deleting.Fraction is null;
            entry.DeletingBar.Fraction = deleting.Fraction ?? 0;
            entry.DeletingBar.Text = deleting.Text;
            SetBar(c, entry.DeletingBar);
        }
        else if (v.Segs.Count == 0)
        {
            entry.Placeholder ??= new TextBlock { Style = AppResources.Get<Style>("BarPlaceholderTextStyle") };
            entry.Placeholder.Text = v.Placeholder;
            SetBar(c, entry.Placeholder);
        }
        else
        {
            entry.Panel ??= new ProportionalPanel();
            UpdateSegments(entry, v);
            SetBar(c, entry.Panel);
        }
    }

    private static void SetBar(RowControl c, UIElement content)
    {
        if (!ReferenceEquals(c.BarContent, content))
        {
            c.BarContent = content;
        }
    }

    private void ApplyBusy(RowEntry entry)
    {
        bool locked = _busy && entry.View.Deleting is null;
        if (entry.Leaving)
        {
            return;
        }
        entry.Control.IsEnabled = !locked;
        entry.Control.Opacity = locked ? 0.45 : 1;
    }

    // ---------- Segments ----------

    private void UpdateSegments(RowEntry entry, RowView v)
    {
        ProportionalPanel panel = entry.Panel!;
        var seen = new HashSet<string>();
        for (int i = 0; i < v.Segs.Count; i++)
        {
            SegView s = v.Segs[i];
            seen.Add(s.Key);
            if (!entry.Segments.TryGetValue(s.Key, out SegEntry? seg))
            {
                seg = CreateSegment(entry, s.Key);
                entry.Segments[s.Key] = seg;
            }
            UpdateSegment(seg, s, v);
            panel.PlaceLive(seg.Button, i);
        }
        foreach ((string key, SegEntry seg) in entry.Segments.ToList())
        {
            if (!seen.Contains(key))
            {
                // The panel shrinks it into the seam it leaves (a segment folded into "(small N)", a scan's
                // "unscanned" remainder reaching zero) before detaching it.
                entry.Segments.Remove(key);
                panel.RemoveAnimated(seg.Button);
            }
        }
    }

    private SegEntry CreateSegment(RowEntry entry, string key)
    {
        var button = new SegmentButton();
        var seg = new SegEntry(button);
        button.Click += (_, _) =>
        {
            if (seg.View is { Expandable: true } s)
            {
                SegmentInvoked?.Invoke(entry.View, s);
            }
        };
        // A new drive segment has no fill element until its template is applied; redraw the ribbons then.
        button.Loaded += (_, _) => UpdateRibbons(entry.Card);
        AutomationProperties.SetAutomationId(button, $"seg:{key}");
        // The tooltip's content is built when it opens. Hover text changes with every poll while a scan runs,
        // and building a panel per segment per poll was the largest single cost of a poll on the UI thread.
        var tip = new ToolTip();
        tip.Opened += (_, _) =>
        {
            if (seg.View is { } view)
            {
                tip.Content = ToolTipFor(view.Hover);
            }
        };
        tip.Closed += (_, _) => tip.Content = null;
        ToolTipService.SetToolTip(button, tip);
        seg.Tip = tip;
        return seg;
    }

    private static void UpdateSegment(SegEntry seg, SegView s, RowView row)
    {
        SegView? old = seg.View;
        seg.View = s;
        SegmentButton b = seg.Button;
        ProportionalPanel.SetLeft(b, s.Left);
        ProportionalPanel.SetSpan(b, s.Width);
        b.Appearance = Appearance(s.Style);
        b.SegmentColor = s.Color.ToColor();
        b.Text = s.Text;
        b.IsExpanded = row.Expanded == s.Key;
        b.FillFraction = row.Kind == RowKind.Drive && row.Fill is { } fill ? fill : double.NaN;
        b.IsProgressIndeterminate = s.Progress is { Fraction: null };
        b.ProgressFraction = s.Progress?.Fraction ?? double.NaN;
        // Only expandable segments are tab stops, as in the web UI.
        b.IsTabStop = s.Expandable;
        if (old is null || !old.Hover.Equals(s.Hover))
        {
            if (seg.Tip is { IsOpen: true } tip)
            {
                tip.Content = ToolTipFor(s.Hover);
            }
            AutomationProperties.SetName(b, s.Hover.Lines.Count > 0
                ? $"{s.Hover.Title}, {string.Join(", ", s.Hover.Lines)}"
                : s.Hover.Title);
        }
    }

    private static SegmentAppearance Appearance(SegStyle style) => style switch
    {
        SegStyle.Drive => SegmentAppearance.Drive,
        SegStyle.Small => SegmentAppearance.Small,
        SegStyle.Error => SegmentAppearance.Error,
        SegStyle.Pending => SegmentAppearance.Pending,
        SegStyle.Files => SegmentAppearance.Files,
        SegStyle.Other => SegmentAppearance.Other,
        SegStyle.Hatched => SegmentAppearance.Hatched,
        _ => SegmentAppearance.Plain,
    };

    /// <summary>Tooltip replacing the web UI's hover overlay: bold title (path) over the detail lines.</summary>
    private static StackPanel ToolTipFor(HoverInfo hover)
    {
        _tooltipsBuilt++;
        var panel = new StackPanel { Spacing = 2, MaxWidth = 560 };
        panel.Children.Add(new TextBlock
        {
            Text = hover.Title,
            Style = AppResources.Get<Style>("ToolTipTitleTextStyle"),
        });
        foreach (string line in hover.Lines)
        {
            panel.Children.Add(new TextBlock
            {
                Text = line,
                Style = AppResources.Get<Style>("ToolTipLineTextStyle"),
            });
        }
        return panel;
    }

    // ---------- Ribbons ----------

    /// <summary>
    /// drawSplines: every row that expands a segment of the row above gets a ribbon from that segment to its own
    /// bar. <c>From == "used"</c> anchors on the drive row's used fill; a key that is no longer visible (folded into
    /// the row's leftover: "(other N)" on folder rows, "(small N)" on files rows) falls back to that segment.
    /// </summary>
    private void UpdateRibbons(Card card)
    {
        var specs = new List<RibbonSpec>();
        IReadOnlyList<RowView> views = card.Views;
        for (int i = 1; i < views.Count; i++)
        {
            RowView child = views[i];
            if (child.From is not { } from
                || !_rows.TryGetValue(views[i - 1].Key, out RowEntry? parent)
                || !_rows.TryGetValue(child.Key, out RowEntry? childEntry))
            {
                continue;
            }
            FrameworkElement? anchor = from == "used"
                ? parent.Segments.TryGetValue("drive", out SegEntry? drive) ? drive.Button.FillElement : null
                : parent.Segments.TryGetValue(from, out SegEntry? seg) ? seg.Button
                : parent.Segments.TryGetValue("other", out SegEntry? other) ? other.Button
                : parent.Segments.TryGetValue("small", out SegEntry? small) ? small.Button
                : null;
            if (anchor is null || parent.View.Segs.Count == 0)
            {
                continue;
            }
            specs.Add(new RibbonSpec(anchor, childEntry.Control.Bar, child.Edge is { } e ? e.ToColor() : FallbackEdge));
        }
        card.Ribbons.SetRibbons(specs);
    }

    // ---------- Animation and placement ----------

    /// <summary>Grows a freshly inserted row (and its gap) out of the row above it (the web UI's animateRowIn).</summary>
    private static void AnimateOpen(RowSlot slot)
    {
        if (!Motion.Enabled)
        {
            return;
        }
        // Start collapsed and invisible; the animation needs the row's open height, which the first layout measures.
        slot.MaxHeight = 0;
        slot.Opacity = 0;
        bool started = false;
        slot.SizeChanged += (_, _) =>
        {
            if (started)
            {
                return;
            }
            started = true;
            if (slot.OpenHeight <= 0)
            {
                Open(slot);
                return;
            }
            // Begin on the next frame, not inside the layout pass that sized the row: a storyboard begun here
            // would start from this frame's time and, after a long pass, be seen first well into its run.
            FrameQueue.Next(() =>
            {
                Storyboard storyboard = RowStoryboard(slot, heightFrom: 0, heightTo: slot.OpenHeight, opacityFrom: 0, opacityTo: 1);
                storyboard.Completed += (_, _) =>
                {
                    Open(slot);
                    storyboard.Stop(); // drop the held animated values; the local values set by Open take over
                };
                storyboard.Begin();
            });
        };
    }

    /// <summary>Puts a slot in its fully open, unanimated state.</summary>
    private static void Open(RowSlot slot)
    {
        slot.MaxHeight = double.PositiveInfinity;
        slot.Opacity = 1;
    }

    /// <summary>Shrinks a removed row (and its gap) back into the row above, then detaches it (animateRowOut).</summary>
    private void AnimateClosedAndRemove(RowEntry entry)
    {
        entry.Leaving = true;
        RowControl control = entry.Control;
        control.IsHitTestVisible = false;
        control.IsTabStop = false;
        void Detach()
        {
            if (entry.Slot.Parent is Panel parent)
            {
                parent.Children.Remove(entry.Slot);
            }
            UpdateRibbons(entry.Card);
        }
        if (!Motion.Enabled)
        {
            Detach();
            return;
        }
        double open = entry.Slot.OpenHeight > 0 ? entry.Slot.OpenHeight : entry.Slot.ActualHeight;
        if (open <= 0)
        {
            Detach();
            return;
        }
        Storyboard storyboard = RowStoryboard(entry.Slot, heightFrom: open, heightTo: 0, opacityFrom: entry.Slot.Opacity, opacityTo: 0);
        storyboard.Completed += (_, _) => Detach();
        storyboard.Begin();
    }

    /// <summary>
    /// Animates a slot's MaxHeight (gap and height: a layout animation, so the rows below move and the ribbons
    /// follow every frame) and its opacity together, with the web UI's easing. The first key frame pins the start
    /// values so a new row never flashes at full size before the animation's first tick.
    /// </summary>
    private static Storyboard RowStoryboard(RowSlot slot, double heightFrom, double heightTo, double opacityFrom, double opacityTo)
    {
        var storyboard = new Storyboard();
        storyboard.Children.Add(KeyFrames(slot, nameof(FrameworkElement.MaxHeight), heightFrom, heightTo));
        storyboard.Children.Add(KeyFrames(slot, nameof(UIElement.Opacity), opacityFrom, opacityTo));
        return storyboard;
    }

    private static DoubleAnimationUsingKeyFrames KeyFrames(DependencyObject target, string property, double from, double to)
    {
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = from });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(Motion.RowDuration),
            Value = to,
            // cubic-bezier(.33, 0, .2, 1), the web UI's ROW_EASE; the segment glide uses the same curve.
            KeySpline = new KeySpline { ControlPoint1 = Motion.RowEaseIn, ControlPoint2 = Motion.RowEaseOut },
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private int SkipLeaving(UIElementCollection children, int position)
    {
        while (position < children.Count && children[position] is RowSlot slot && IsLeaving(slot))
        {
            position++;
        }
        return position;
    }

    private bool IsLeaving(RowSlot slot)
    {
        foreach (RowEntry e in _rows.Values)
        {
            if (ReferenceEquals(e.Slot, slot))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Puts <paramref name="element"/> at <paramref name="index"/>, moving rather than re-adding it.</summary>
    private static void PlaceAt(UIElementCollection children, UIElement element, int index)
    {
        int current = children.IndexOf(element);
        if (current == index)
        {
            return;
        }
        if (current < 0)
        {
            children.Insert(Math.Min(index, children.Count), element);
        }
        else
        {
            children.Move((uint)current, (uint)Math.Min(index, children.Count - 1));
        }
    }

    /// <summary>A drive card: rounded border, the ribbon layer behind the rows, and the row stack.</summary>
    private sealed class Card
    {
        public Card(string letter)
        {
            // No Spacing here: each RowSlot carries its own gap so the gap can open and close with the row.
            Rows = new StackPanel();
            Ribbons = new RibbonLayer();
            var grid = new Grid();
            // Ribbons sit above the card background but behind the rows (the web UI's z-order).
            grid.Children.Add(Ribbons);
            grid.Children.Add(Rows);
            Root = new Border { Style = AppResources.Get<Style>("DriveCardStyle"), Child = grid };
            AutomationProperties.SetAutomationId(Root, $"drive:{letter}");
        }

        public Border Root { get; }

        public RibbonLayer Ribbons { get; }

        public StackPanel Rows { get; }

        public IReadOnlyList<RowView> Views { get; set; } = Array.Empty<RowView>();
    }

    private sealed class RowEntry(Card card, RowSlot slot, RowControl control, RowView view)
    {
        public Card Card { get; } = card;

        /// <summary>The animated host of <see cref="Control"/>; this is what sits in the card's row stack.</summary>
        public RowSlot Slot { get; } = slot;

        public RowControl Control { get; } = control;

        public RowView View { get; set; } = view;

        public bool Leaving { get; set; }

        public ProportionalPanel? Panel { get; set; }

        public TextBlock? Placeholder { get; set; }

        public DeletingBar? DeletingBar { get; set; }

        public Dictionary<string, SegEntry> Segments { get; } = [];
    }

    private sealed class SegEntry(SegmentButton button)
    {
        public SegmentButton Button { get; } = button;

        public SegView? View { get; set; }

        /// <summary>The segment's tooltip; its content is built from <see cref="View"/> when it opens.</summary>
        public ToolTip? Tip { get; set; }
    }
}
