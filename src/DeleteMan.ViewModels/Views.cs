// Row and segment views. Records holding lists compare those lists element by element, so the UI can diff rows.

namespace DeleteMan.ViewModels;

/// <summary>Segment appearance (the old CSS class).</summary>
public enum SegStyle
{
    Plain,
    Drive,
    Small,
    Error,
    Pending,
    Files,
    Other,
    Hatched,
}

public enum RowKind
{
    Drive,
    Used,
    Dir,
    Files,
    Small,
    File,
    Other,
}

public enum RowControl
{
    Close,
    Recycle,
    Delete,
}

public sealed record HoverInfo(string Title, IReadOnlyList<string> Lines)
{
    public bool Equals(HoverInfo? other) =>
        other is not null && Title == other.Title && Seq.Equal(Lines, other.Lines);

    public override int GetHashCode() => HashCode.Combine(Title, Seq.Hash(Lines));
}

/// <param name="Key">Segment key, unique within the row.</param>
/// <param name="Text">Label drawn in the segment.</param>
/// <param name="Left">Offset in percent of the bar.</param>
/// <param name="Width">Width in percent of the bar.</param>
/// <param name="Color">Fill colour.</param>
/// <param name="Style">Appearance.</param>
/// <param name="Expandable">Clicking opens a row below.</param>
/// <param name="Progress">Deletion progress of the segment's target, if any.</param>
/// <param name="Hover">Tooltip.</param>
public sealed record SegView(
    string Key,
    string Text,
    double Left,
    double Width,
    Rgb Color,
    SegStyle Style,
    bool Expandable,
    ProgressView? Progress,
    HoverInfo Hover);

/// <param name="Key">Position key (<see cref="Chain.RowPosKey"/>).</param>
/// <param name="Kind">Row kind.</param>
/// <param name="Letter">Drive letter.</param>
/// <param name="Index">Index in the drive's chain; -1 for the drive row itself.</param>
/// <param name="Label">Row name.</param>
/// <param name="Title">Row tooltip / full path.</param>
/// <param name="Sub">Second line.</param>
/// <param name="Size">Size text.</param>
/// <param name="Edge">Left border / spline colour = colour of the parent segment.</param>
/// <param name="From">Segment key in the row above that this row expands.</param>
/// <param name="Fill">Drive rows: used fraction of capacity.</param>
/// <param name="Segs">Bar segments.</param>
/// <param name="Placeholder">Shown in the bar when there are no segments.</param>
/// <param name="Expanded">Key of this row's expanded segment.</param>
/// <param name="Controls">Buttons at the end of the row.</param>
/// <param name="Deleting">The row's own deletion progress, replacing the controls.</param>
/// <param name="Pin">The pin toggle (<see cref="PinState.None"/> = no button).</param>
public sealed record RowView(
    string Key,
    RowKind Kind,
    string Letter,
    int Index,
    string Label,
    string Title,
    string Sub,
    string Size,
    Rgb? Edge,
    string? From,
    double? Fill,
    IReadOnlyList<SegView> Segs,
    string Placeholder,
    string? Expanded,
    IReadOnlyList<RowControl> Controls,
    DeletingView? Deleting,
    PinState Pin = PinState.None)
{
    public bool Equals(RowView? other) =>
        other is not null
        && Key == other.Key
        && Kind == other.Kind
        && Letter == other.Letter
        && Index == other.Index
        && Label == other.Label
        && Title == other.Title
        && Sub == other.Sub
        && Size == other.Size
        && Edge == other.Edge
        && From == other.From
        && Fill == other.Fill
        && Placeholder == other.Placeholder
        && Expanded == other.Expanded
        && Deleting == other.Deleting
        && Pin == other.Pin
        && Seq.Equal(Segs, other.Segs)
        && Seq.Equal(Controls, other.Controls);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Key);
        h.Add(Kind);
        h.Add(Letter);
        h.Add(Index);
        h.Add(Label);
        h.Add(Title);
        h.Add(Sub);
        h.Add(Size);
        h.Add(Edge);
        h.Add(From);
        h.Add(Fill);
        h.Add(Placeholder);
        h.Add(Expanded);
        h.Add(Deleting);
        h.Add(Pin);
        h.Add(Seq.Hash(Segs));
        h.Add(Seq.Hash(Controls));
        return h.ToHashCode();
    }
}

/// <summary>Element-wise list equality and hashing.</summary>
internal static class Seq
{
    public static bool Equal<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        var cmp = EqualityComparer<T>.Default;
        for (var i = 0; i < a.Count; i++)
        {
            if (!cmp.Equals(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }

    public static int Hash<T>(IReadOnlyList<T> items)
    {
        var h = new HashCode();
        h.Add(items.Count);
        foreach (var item in items)
        {
            h.Add(item);
        }
        return h.ToHashCode();
    }
}
