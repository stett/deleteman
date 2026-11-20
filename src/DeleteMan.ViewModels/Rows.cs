// Row views (model.ts buildRows and the row/segment builders).

using DeleteMan.Engine;

namespace DeleteMan.ViewModels;

public static class Rows
{
    /// <summary>Segments narrower than this fraction of the bar are folded into the row's leftover segment.</summary>
    private const double SmallFraction = 0.0025;

    /// <summary>
    /// On a page of leftover folders, the share of the bar given to what lies beyond the page: proportional to its
    /// bytes within these bounds, so the page stays readable however much lies beyond it, and the segment stays
    /// clickable however little.
    /// </summary>
    private const double MinRestShare = 0.04;

    /// <inheritdoc cref="MinRestShare"/>
    private const double MaxRestShare = 0.25;

    /// <summary>
    /// Non-breaking space used as the sub-line while a row's data loads, so the row keeps its two-line height from
    /// the moment it is inserted.
    /// </summary>
    private const string SubPlaceholder = " ";

    private static readonly RowControl[] CloseOnly = [RowControl.Close];
    private static readonly RowControl[] DirControls = [RowControl.Close, RowControl.Recycle, RowControl.Delete];

    /// <summary>A segment before layout: what it stands for, its size, and how it looks.</summary>
    internal sealed record RawSeg(
        string Key,
        string Text,
        ulong Bytes,
        Rgb Color,
        HoverInfo Hover,
        SegStyle Style = SegStyle.Plain,
        bool Expandable = false,
        bool Foldable = false,
        ProgressView? Progress = null);

    /// <summary>
    /// What a row's folded items become: the key of the one segment that stands for them (and so of the row it
    /// opens), whether it opens one, the noun of the items, and the items beyond the row's window, which the engine
    /// did not list and which join the fold.
    /// </summary>
    private sealed record Rest(string Key, bool Opens, string Noun, ulong MoreCount = 0, ulong MoreBytes = 0);

    /// <summary>Tiny files of a files row fold into "(small N)", which opens them as a small row.</summary>
    private static readonly Rest FilesRest = new("small", Opens: true, "file");

    /// <summary>
    /// A folder row's leftover: the subfolders too small to draw, and the ones past its window (the row draws from
    /// the largest <see cref="Chain.MaxChildren"/>; the engine lists only those unless pages are open and folds
    /// the rest for it), become one "other" segment: "(small N)" when every subfolder was listed, "(other N)" when
    /// some were not. Either opens a page of them (<see cref="OtherRow"/>).
    /// </summary>
    private static Rest FolderRest(NodeView node)
    {
        var (count, bytes) = Beyond(node);
        return new Rest("other", Opens: true, "folder", count, bytes);
    }

    /// <summary>Subfolders of <paramref name="node"/> past its row's window: the listed ones beyond it, and the engine's own fold.</summary>
    private static (ulong Count, ulong Bytes) Beyond(NodeView node)
    {
        var count = node.OtherCount;
        var bytes = node.OtherBytes;
        for (var i = Chain.MaxChildren; i < node.Children.Count; i++)
        {
            count++;
            bytes += node.Children[i].TotalBytes;
        }
        return (count, bytes);
    }

    /// <summary>The whole drill-down flattened in display order.</summary>
    public static IReadOnlyList<RowView> Build(Model m)
    {
        var rows = new List<RowView>();
        foreach (var d in m.Drives)
        {
            IReadOnlyList<ChainEntry> chain = m.Chains.TryGetValue(d.Letter, out var c) ? c : [];
            rows.Add(DriveRow(d, chain.Count > 0) with { Pin = Pins.StateOf(chain, -1) });
            for (var i = 0; i < chain.Count; i++)
            {
                var e = chain[i];
                var expanded = i + 1 < chain.Count ? chain[i + 1].SegKey : null;
                var row = e.Kind switch
                {
                    EntryKind.Used => UsedRow(m, d, e, i, expanded),
                    EntryKind.Dir => DirRow(m, e, d.Letter, i, expanded),
                    EntryKind.Small => SmallRow(m, e, d.Letter, i, expanded),
                    EntryKind.File => FileRow(m, e, d.Letter, i),
                    EntryKind.Other => OtherRow(m, chain, i, d, expanded),
                    _ => FilesRow(m, e, d.Letter, i, expanded),
                };
                // A row being deleted shows Cancel in place of all its controls, the pin included.
                rows.Add(row with { Pin = row.Deleting is null ? Pins.StateOf(chain, i) : PinState.None });
            }
        }
        return rows;
    }

    // ---------- Helpers ----------

    private static ulong UsedBytes(DriveInfo d) => d.Total > d.Free ? d.Total - d.Free : 0;

    private static Rgb SegColor(string name, NodeState state) => state == NodeState.Error ? Palette.Error : Palette.ColorFor(name);

    private static SegStyle StateStyle(NodeState state) => state switch
    {
        NodeState.Error => SegStyle.Error,
        NodeState.Pending or NodeState.Scanning => SegStyle.Pending,
        _ => SegStyle.Plain,
    };

    private static string StateText(NodeState state, uint? code) => state switch
    {
        NodeState.Pending => "waiting to be scanned",
        NodeState.Scanning => "scanning…",
        NodeState.Error => code == 5 ? "Access denied (5)" : $"Error {(code is { } c ? c.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?")}",
        NodeState.Deleted => "deleted",
        _ => "",
    };

    private static string AttrsText(uint attrs)
    {
        var parts = new List<string>();
        if ((attrs & 0x1) != 0)
        {
            parts.Add("read-only");
        }
        if ((attrs & 0x2) != 0)
        {
            parts.Add("hidden");
        }
        if ((attrs & 0x4) != 0)
        {
            parts.Add("system");
        }
        return string.Join(" · ", parts);
    }

    private static string ScanText(ScanStatus? scan) => scan?.Status switch
    {
        null => "starting scan…",
        ScanState.Scanning => $"scanning… depth {scan.Depth} · {Format.Count(scan.DirsScanned)} folders",
        ScanState.Done => "scan complete",
        ScanState.Cancelled => "scan cancelled",
        _ => "waiting to scan…",
    };

    private static string[] NonEmpty(params string[] lines) => lines.Where(l => l.Length > 0).ToArray();

    // ---------- Layout ----------

    /// <summary>Bar denominator: the row's nominal total, or the sum of its segments (listed and beyond) if that is larger.</summary>
    private static double Denominator(IReadOnlyList<RawSeg> raw, ulong total, ulong beyond = 0)
    {
        var sum = beyond;
        foreach (var r in raw)
        {
            sum += r.Bytes;
        }
        return Math.Max(Math.Max(total, sum), 1UL);
    }

    private static bool Tiny(ulong bytes, double denom) => bytes / denom < SmallFraction;

    private static bool IsFolded(RawSeg r, double denom) => r.Foldable && Tiny(r.Bytes, denom);

    /// <summary>
    /// Positions segments in order; tiny foldable ones, with whatever lies beyond the row's window, are merged into
    /// one leftover segment (<see cref="Rest"/>), placed after everything but the drive's hatched remainder.
    /// </summary>
    private static List<SegView> Layout(IReadOnlyList<RawSeg> raw, ulong total, Rest rest)
    {
        var denom = Denominator(raw, total, rest.MoreBytes);
        var main = new List<RawSeg>();
        var tail = new List<RawSeg>();
        ulong foldedCount = 0;
        ulong foldedBytes = 0;
        foreach (var r in raw)
        {
            if (IsFolded(r, denom))
            {
                foldedCount++;
                foldedBytes += r.Bytes;
            }
            else
            {
                (r.Foldable ? main : tail).Add(r);
            }
        }
        if (foldedCount + rest.MoreCount > 0)
        {
            var at = tail.FindIndex(r => r.Key == "unscanned");
            tail.Insert(at < 0 ? tail.Count : at, RestSeg(rest, foldedCount, foldedBytes));
        }
        var left = 0.0;
        var segs = new List<SegView>(main.Count + tail.Count);
        foreach (var r in main.Concat(tail))
        {
            var width = r.Bytes / denom * 100;
            segs.Add(new SegView(r.Key, r.Text, left, width, r.Color, r.Style, r.Expandable, r.Progress, r.Hover));
            left += width;
        }
        return segs;
    }

    /// <summary>
    /// A page of leftover folders at its own scale: the drawn folders share the bar in proportion to one another
    /// (equally when none has bytes), and the page's own leftover takes a share of its bytes clamped to
    /// <see cref="MinRestShare"/>..<see cref="MaxRestShare"/>, so the page stays readable however much lies beyond.
    /// </summary>
    private static List<SegView> LayoutPage(IReadOnlyList<RawSeg> drawn, ulong sum, RawSeg? rest)
    {
        var share = 0.0;
        if (rest is not null)
        {
            var whole = (double)sum + rest.Bytes;
            share = Math.Clamp(whole > 0 ? rest.Bytes / whole : 0, MinRestShare, MaxRestShare);
        }
        var segs = new List<SegView>(drawn.Count + 1);
        var left = 0.0;
        foreach (var r in drawn)
        {
            var fraction = sum > 0 ? r.Bytes / (double)sum : 1.0 / drawn.Count;
            var width = fraction * (1 - share) * 100;
            segs.Add(new SegView(r.Key, r.Text, left, width, r.Color, r.Style, r.Expandable, r.Progress, r.Hover));
            left += width;
        }
        if (rest is not null)
        {
            segs.Add(new SegView(rest.Key, rest.Text, left, share * 100, rest.Color, rest.Style, rest.Expandable, rest.Progress, rest.Hover));
        }
        return segs;
    }

    /// <summary>
    /// The leftover segment: "(small N)" when the folded items are all there is (every item was listed), "(other N)"
    /// when items beyond the window join them.
    /// </summary>
    private static RawSeg RestSeg(Rest rest, ulong foldedCount, ulong foldedBytes)
    {
        var count = foldedCount + rest.MoreCount;
        var bytes = foldedBytes + rest.MoreBytes;
        if (rest.MoreCount == 0)
        {
            var small = $"(small {Format.Count(count)})";
            return new RawSeg(
                rest.Key,
                small,
                bytes,
                Palette.Small,
                new HoverInfo(small, NonEmpty(
                    Format.Bytes(bytes),
                    count == 1 ? $"1 {rest.Noun} under 0.25 % of this bar" : $"{Format.Plural(count, rest.Noun)}, each under 0.25 % of this bar",
                    rest.Opens ? "Click to open them as a row" : "")),
                SegStyle.Small,
                Expandable: rest.Opens);
        }
        var other = $"(other {Format.Count(count)})";
        return new RawSeg(
            rest.Key,
            other,
            bytes,
            Palette.Other,
            new HoverInfo(other, [Format.Bytes(bytes), $"{Format.Plural(count, "more " + rest.Noun)} not shown here", "Click to open them as a row, largest first"]),
            SegStyle.Other,
            Expandable: true);
    }

    // ---------- Segments ----------

    private static RawSeg ChildSeg(Model m, NodeView node, ChildView c)
    {
        var path = Paths.JoinPath(node.Path, c.Name);
        var error = c.State == NodeState.Error;
        return new RawSeg(
            c.Id,
            c.Name,
            c.TotalBytes,
            SegColor(c.Name, c.State),
            new HoverInfo(path, NonEmpty(Format.Bytes(c.TotalBytes), Format.Plural(c.TotalFiles, "file"), StateText(c.State, c.ErrorCode))),
            StateStyle(c.State),
            Expandable: !error,
            Foldable: !error,
            Progress: Jobs.SegProgress(m.Jobs, path));
    }

    /// <summary>
    /// The segments of a folder's row: its largest <see cref="Chain.MaxChildren"/> children (desc) and "(files)";
    /// the leftover segment is <see cref="Layout"/>'s. Built once per change of the node view, the job list or the
    /// folder's file list (all keep their identity across polls while unchanged, see <see cref="Model.ApplyPoll"/>):
    /// the hover text and colours of up to 64 children are the bulk of a row's cost, and most polls change none of
    /// them. Callers get their own list and may extend it.
    /// </summary>
    private static List<RawSeg> DirSegs(Model m, NodeView node)
    {
        // The folder's loose-file list (when fetched) decides whether "(files)" is shown as one file by name.
        var files = m.Files.GetValueOrDefault(node.Id);
        if (!m.SegCache.TryGetValue(node.Id, out var cached)
            || !ReferenceEquals(cached.Node, node)
            || !ReferenceEquals(cached.Jobs, m.Jobs)
            || !ReferenceEquals(cached.Files, files))
        {
            cached = new SegCacheEntry(node, m.Jobs, files, [.. BuildDirSegs(m, node, files)]);
            m.SegCache[node.Id] = cached;
        }
        return [.. cached.Segs];
    }

    private static List<RawSeg> BuildDirSegs(Model m, NodeView node, IReadOnlyList<FileView>? files)
    {
        // Children past the window are listed only because pages of the leftover are open; the row itself keeps
        // drawing from the largest 64, so opening a page changes nothing above it.
        var window = Math.Min(node.Children.Count, Chain.MaxChildren);
        var output = new List<RawSeg>(window + 1);
        for (var i = 0; i < window; i++)
        {
            output.Add(ChildSeg(m, node, node.Children[i]));
        }
        if (node.OwnBytes > 0 || node.OwnFiles > 0)
        {
            output.Add(SingleFile(files) is { } single ? SingleFileSeg(m, node, single, files!.Count - 1) : FilesSeg(node));
        }
        return output;
    }

    /// <summary>"(files)": the loose files of a folder, opening the files row.</summary>
    private static RawSeg FilesSeg(NodeView node) => new(
        "files",
        "(files)",
        node.OwnBytes,
        Palette.Files,
        new HoverInfo($"(files) in {node.Path}", [Format.Bytes(node.OwnBytes), Format.Plural(node.OwnFiles, "file")]),
        SegStyle.Files,
        Expandable: node.OwnFiles > 0);

    /// <summary>
    /// The loose files of a folder when they amount to one file: shown by name, in the files colour and at the
    /// loose files' size, opening the file's row directly so the file is one click from its delete buttons.
    /// <paramref name="others"/> smaller files fold away behind it.
    /// </summary>
    private static RawSeg SingleFileSeg(Model m, NodeView node, FileView file, int others)
    {
        var path = Paths.JoinPath(node.Path, file.Name);
        return new RawSeg(
            Chain.FileKeyPrefix + file.Name,
            file.Name,
            node.OwnBytes,
            Palette.Files,
            new HoverInfo(path, NonEmpty(
                Format.Bytes(file.Size),
                AttrsText(file.Attrs),
                others > 0 ? $"plus {Format.Plural((ulong)others, "smaller file")} under 0.25 % of the loose bytes" : "")),
            SegStyle.Files,
            Expandable: true,
            Progress: Jobs.SegProgress(m.Jobs, path));
    }

    /// <summary>
    /// The one file a folder's loose files amount to: its only file, or the only one that would show in a files
    /// row (every other one folds under 0.25 % of the loose bytes). Null when the list is not here or several show.
    /// </summary>
    internal static FileView? SingleFile(IReadOnlyList<FileView>? files)
    {
        if (files is null || files.Count == 0)
        {
            return null;
        }
        if (files.Count == 1)
        {
            return files[0];
        }
        ulong sum = 0;
        foreach (var f in files)
        {
            sum += f.Size;
        }
        var denom = Math.Max(sum, 1UL);
        FileView? shown = null;
        foreach (var f in files)
        {
            if (f.Size / (double)denom >= SmallFraction)
            {
                if (shown is not null)
                {
                    return null;
                }
                shown = f;
            }
        }
        return shown;
    }

    /// <summary>Root folders + "(files)" + the hatched unscanned/unattributed remainder of a drive.</summary>
    private static List<RawSeg> UsedRaw(Model m, DriveInfo d, NodeView node)
    {
        var used = UsedBytes(d);
        var scan = m.Scans.GetValueOrDefault(d.Letter);
        var raw = DirSegs(m, node);
        var done = scan?.Status == ScanState.Done;
        var accounted = scan?.AccountedBytes ?? node.TotalBytes;
        var rest = used > accounted ? used - accounted : 0;
        if (rest > 0)
        {
            var label = done ? "other/system" : "unscanned";
            raw.Add(new RawSeg(
                "unscanned",
                label,
                rest,
                Palette.Hatch,
                new HoverInfo(label,
                [
                    Format.Bytes(rest),
                    done
                        ? "Used space the scan could not attribute (file system metadata, protected folders, shadow copies)"
                        : "Not scanned yet",
                ]),
                SegStyle.Hatched));
        }
        return raw;
    }

    // ---------- Leftover pages ----------

    private static bool Foldable(ChildView c) => c.State != NodeState.Error;

    /// <summary>
    /// Splits a page of leftover folders into the ones drawn and the ones folded on to the next page: the readable
    /// ones under 0.25 % of the page's own bytes (unreadable folders are always drawn, as slivers). The largest of
    /// a page is never folded, so every page draws something. A page with no bytes at all draws every folder, at
    /// equal widths, since there is nothing to tell them apart by.
    /// </summary>
    private static (List<ChildView> Drawn, List<ChildView> Folded) SplitPage(IReadOnlyList<ChildView> page)
    {
        ulong sum = 0;
        foreach (var c in page)
        {
            sum += c.TotalBytes;
        }
        var drawn = new List<ChildView>(page.Count);
        var folded = new List<ChildView>();
        foreach (var c in page)
        {
            (sum > 0 && Foldable(c) && Tiny(c.TotalBytes, sum) ? folded : drawn).Add(c);
        }
        return (drawn, folded);
    }

    /// <summary>
    /// The leftover folders of <paramref name="node"/> at page <paramref name="pagesAbove"/> below its row
    /// (<paramref name="owner"/>, its used or dir entry): the subfolders the row did not draw (folded as tiny, or
    /// past its window) and the pages above did not draw either, largest first, as far as the engine has listed
    /// them. With it, how many folders the row and the pages above drew, and how many of the leftover the page
    /// just above folded (the row's own fold when there is none).
    /// </summary>
    private static (List<ChildView> Left, int Shown, int FoldedAbove) Leftover(Model m, DriveInfo d, ChainEntry owner, NodeView node, int pagesAbove)
    {
        var (raw, total) = owner.Kind == EntryKind.Used
            ? (UsedRaw(m, d, node), UsedBytes(d))
            : (DirSegs(m, node), node.TotalBytes);
        var denom = Denominator(raw, total, Beyond(node).Bytes);
        var left = new List<ChildView>();
        var shown = 0;
        var window = Math.Min(Chain.MaxChildren, node.Children.Count);
        for (var i = 0; i < window; i++)
        {
            var c = node.Children[i];
            if (Foldable(c) && Tiny(c.TotalBytes, denom))
            {
                left.Add(c);
            }
            else
            {
                shown++;
            }
        }
        var foldedAbove = left.Count;
        for (var i = window; i < node.Children.Count; i++)
        {
            left.Add(node.Children[i]);
        }
        for (var p = 0; p < pagesAbove; p++)
        {
            var (drawn, folded) = SplitPage(left.Take(Chain.MaxChildren).ToList());
            shown += drawn.Count;
            foldedAbove = folded.Count;
            left = [.. folded, .. left.Skip(Chain.MaxChildren)];
        }
        return (left, shown, foldedAbove);
    }

    // ---------- Rows ----------

    private static RowView DriveRow(DriveInfo d, bool open)
    {
        var used = UsedBytes(d);
        var name = d.Label.Length > 0 ? $"{d.Letter}:  {d.Label}" : $"{d.Letter}:";
        var kind = d.Kind != DriveKind.Fixed ? d.Kind.ToString().ToLowerInvariant() : "";
        var seg = new SegView(
            "drive",
            $"{Format.Bytes(used)} used · {Format.Bytes(d.Free)} free",
            0,
            100,
            Palette.DriveTrack,
            SegStyle.Drive,
            Expandable: true,
            Progress: null,
            new HoverInfo(name, [$"Used {Format.Bytes(used)} · Free {Format.Bytes(d.Free)} · {d.Fs}"]));
        return new RowView(
            Chain.RowPosKey(d.Letter, -1),
            RowKind.Drive,
            d.Letter,
            -1,
            name,
            $"{d.Letter}:\\ {d.Label}".Trim(),
            string.Join(" · ", NonEmpty(d.Fs, kind)),
            Format.Bytes(d.Total),
            Edge: null,
            From: null,
            Fill: d.Total > 0 ? (double)used / d.Total : 0,
            [seg],
            Placeholder: "",
            Expanded: open ? "drive" : null,
            // Close collapses the drive, so it can act only while the drive is open (the UI shows it disabled otherwise).
            open ? CloseOnly : [],
            Deleting: null);
    }

    private static RowView UsedRow(Model m, DriveInfo d, ChainEntry e, int index, string? expanded)
    {
        var used = UsedBytes(d);
        var node = m.Nodes.GetValueOrDefault(e.NodeId);
        var scan = m.Scans.GetValueOrDefault(d.Letter);
        var row = new RowView(
            Chain.RowPosKey(d.Letter, index),
            RowKind.Used,
            d.Letter,
            index,
            $"{d.Letter}:\\",
            $"{d.Letter}:\\",
            ScanText(scan),
            Format.Bytes(used),
            Palette.Accent,
            e.SegKey,
            Fill: null,
            [],
            "loading…",
            expanded,
            CloseOnly,
            Deleting: null);
        if (node is null)
        {
            return row;
        }
        return row with { Segs = Layout(UsedRaw(m, d, node), used, FolderRest(node)), Placeholder = "" };
    }

    /// <summary>The folded "(small N)" files of the files row above, shown at their own scale so each can be opened.</summary>
    private static RowView SmallRow(Model m, ChainEntry e, string letter, int index, string? expanded)
    {
        var node = m.Nodes.GetValueOrDefault(e.NodeId);
        var row = new RowView(
            Chain.RowPosKey(letter, index),
            RowKind.Small,
            letter,
            index,
            "(small)",
            "",
            SubPlaceholder,
            "",
            Palette.Small,
            e.SegKey,
            Fill: null,
            [],
            "loading…",
            expanded,
            CloseOnly,
            Deleting: null);
        if (node is null || !m.Files.ContainsKey(e.NodeId))
        {
            return row;
        }

        var (raw, total) = FileSegs(m, e.NodeId);
        var denom = Denominator(raw, total);
        // The folded files are all drawn here, whatever their share of each other: this row is the end of the line.
        var folded = raw.Where(r => IsFolded(r, denom)).Select(r => r with { Foldable = false }).ToList();
        ulong sum = 0;
        foreach (var r in folded)
        {
            sum += r.Bytes;
        }
        var count = (ulong)folded.Count;
        return row with
        {
            Label = $"(small {Format.Count(count)})",
            Title = $"Files under 0.25 % of {node.Path}",
            Sub = $"{Format.Plural(count, "file")} under 0.25 % of {node.Name}",
            Size = Format.Bytes(sum),
            Segs = Layout(folded, sum, FilesRest),
            Placeholder = folded.Count == 0 ? "nothing folded" : "",
        };
    }

    /// <summary>
    /// A page of a folder's leftover: the subfolders its row did not draw (too small, or past its window), largest
    /// first, at their own scale so each can be opened. The page is the next <see cref="Chain.MaxChildren"/> of
    /// them; those under 0.25 % of the page, with everything beyond it, are the page's own leftover segment, which
    /// opens the next page. Every page is an <see cref="EntryKind.Other"/> entry of the same node, so the chain
    /// holds the page number and the model asks the engine for enough subfolders (<see cref="Model.NodeRequests"/>).
    /// </summary>
    private static RowView OtherRow(Model m, IReadOnlyList<ChainEntry> chain, int index, DriveInfo d, string? expanded)
    {
        var e = chain[index];
        var pagesAbove = 0;
        var at = index - 1;
        while (at >= 0 && chain[at].Kind == EntryKind.Other && chain[at].NodeId == e.NodeId)
        {
            pagesAbove++;
            at--;
        }
        var owner = at >= 0 && chain[at].NodeId == e.NodeId ? chain[at] : null;
        var node = m.Nodes.GetValueOrDefault(e.NodeId);
        var row = new RowView(
            Chain.RowPosKey(d.Letter, index),
            RowKind.Other,
            d.Letter,
            index,
            "(other)",
            "",
            SubPlaceholder,
            "",
            Palette.Other,
            e.SegKey,
            Fill: null,
            [],
            "loading…",
            expanded,
            CloseOnly,
            Deleting: null);
        if (node is null || owner is null)
        {
            return row;
        }

        var (left, shown, foldedAbove) = Leftover(m, d, owner, node, pagesAbove);
        var pageSize = Math.Min(Chain.MaxChildren, left.Count);
        var (drawn, folded) = SplitPage(left.Take(pageSize).ToList());
        ulong sum = 0;
        foreach (var c in drawn)
        {
            sum += c.TotalBytes;
        }
        ulong foldedBytes = 0;
        foreach (var c in folded)
        {
            foldedBytes += c.TotalBytes;
        }
        // Beyond this page: the listed leftover past it and the engine's fold; with this page's folded folders they
        // make the page's own leftover segment.
        var beyondCount = node.OtherCount;
        var beyondBytes = node.OtherBytes;
        for (var i = pageSize; i < left.Count; i++)
        {
            beyondCount++;
            beyondBytes += left[i].TotalBytes;
        }
        // The segment that opened this page stands for all the leftover from here down. It read "(other …)" when
        // some of that lay beyond the page above (or the row's window), "(small …)" when all of it was that page's
        // own fold; the row says the same.
        var count = (ulong)left.Count + node.OtherCount;
        var isOther = (ulong)(left.Count - foldedAbove) + node.OtherCount > 0;
        var total = node.OtherBytes;
        foreach (var c in left)
        {
            total += c.TotalBytes;
        }

        var raw = drawn.Select(c => ChildSeg(m, node, c)).ToList();
        var rest = folded.Count > 0 || beyondCount > 0
            ? RestSeg(new Rest("other", Opens: true, "folder", beyondCount, beyondBytes), (ulong)folded.Count, foldedBytes)
            : null;
        var directTotal = Format.Count((ulong)node.Children.Count + node.OtherCount);
        var first = (ulong)shown + 1;
        var last = (ulong)(shown + drawn.Count);
        var sub = drawn.Count == 0 ? SubPlaceholder
            : !isOther && pagesAbove == 0 ? $"{Format.Plural(count, "folder")} under 0.25 % of {node.Name}"
            : first == last ? $"Folder {Format.Count(first)} of {directTotal} in {node.Name}"
            : $"Folders {Format.Count(first)}–{Format.Count(last)} of {directTotal} in {node.Name}, largest first";
        return row with
        {
            Label = isOther ? $"(other {Format.Count(count)})" : $"(small {Format.Count(count)})",
            Title = isOther ? $"Folders in {node.Path} not shown on its row, largest first" : $"Folders under 0.25 % of {node.Path}",
            Sub = sub,
            Size = Format.Bytes(total),
            Edge = isOther ? Palette.Other : Palette.Small,
            // Nothing drawn means nothing listed yet: the page's folders come with the next poll (or there are none).
            Segs = drawn.Count > 0 ? LayoutPage(raw, sum, rest) : [],
            Placeholder = drawn.Count > 0 ? "" : count > 0 ? "loading…" : "nothing folded",
        };
    }

    private static RowView DirRow(Model m, ChainEntry e, string letter, int index, string? expanded)
    {
        var node = m.Nodes.GetValueOrDefault(e.NodeId);
        var row = new RowView(
            Chain.RowPosKey(letter, index),
            RowKind.Dir,
            letter,
            index,
            node?.Name ?? "…",
            node?.Path ?? "",
            SubPlaceholder,
            "",
            Edge: null,
            e.SegKey,
            Fill: null,
            [],
            "loading…",
            expanded,
            CloseOnly,
            Deleting: null);
        if (node is null)
        {
            return row;
        }

        var job = RowJob(m.Jobs, node);
        var segs = Layout(DirSegs(m, node), node.TotalBytes, FolderRest(node));
        var placeholder = "";
        if (segs.Count == 0)
        {
            placeholder = node.State == NodeState.Done ? "empty folder" : StateText(node.State, node.ErrorCode);
        }
        DeletingView? deleting = null;
        if (job is not null)
        {
            var p = Jobs.Progress(job);
            deleting = new DeletingView(job.Id, p.Fraction, p.Text);
        }
        return row with
        {
            Sub = $"{Format.Plural(node.TotalFiles, "file")} · {Format.Plural(node.TotalDirs, "folder")}",
            Size = Format.Bytes(node.TotalBytes),
            Edge = SegColor(node.Name, node.State),
            Segs = segs,
            Placeholder = placeholder,
            Controls = job is not null ? [] : DirControls,
            Deleting = deleting,
        };
    }

    /// <summary>
    /// The job deleting this row's folder itself. <c>DeletingJobId</c> is trusted only when the job's path is the
    /// folder's path, so a single-file job inside the folder does not turn the whole row into a progress bar.
    /// </summary>
    private static JobView? RowJob(IReadOnlyList<JobView> jobs, NodeView node)
    {
        if (node.DeletingJobId is { } id)
        {
            var j = jobs.FirstOrDefault(x => x.Id == id);
            if (j is not null && Jobs.IsActive(j) && string.Equals(j.Path, node.Path, StringComparison.OrdinalIgnoreCase))
            {
                return j;
            }
        }
        return Jobs.ActiveJobFor(jobs, node.Path);
    }

    /// <summary>
    /// The loose files of folder <paramref name="nodeId"/> as segments, in the order the engine listed them (largest
    /// first), with their total. Each opens as its own <see cref="FileRow"/>.
    /// </summary>
    private static (List<RawSeg> Segs, ulong Sum) FileSegs(Model m, string nodeId)
    {
        var dir = m.Nodes.GetValueOrDefault(nodeId)?.Path ?? "";
        ulong sum = 0;
        var raw = new List<RawSeg>();
        foreach (var f in m.Files.GetValueOrDefault(nodeId) ?? [])
        {
            sum += f.Size;
            var path = Paths.JoinPath(dir, f.Name);
            raw.Add(new RawSeg(
                Chain.FileKeyPrefix + f.Name,
                f.Name,
                f.Size,
                Palette.ColorFor(f.Name),
                new HoverInfo(path, NonEmpty(Format.Bytes(f.Size), AttrsText(f.Attrs))),
                Expandable: true,
                Foldable: true,
                Progress: Jobs.SegProgress(m.Jobs, path)));
        }
        return (raw, sum);
    }

    private static RowView FilesRow(Model m, ChainEntry e, string letter, int index, string? expanded)
    {
        var node = m.Nodes.GetValueOrDefault(e.NodeId);
        var files = m.Files.GetValueOrDefault(e.NodeId);
        var dir = node?.Path ?? "";
        var (raw, sum) = FileSegs(m, e.NodeId);
        var sub = SubPlaceholder;
        if (files is not null)
        {
            sub = Format.Plural((ulong)files.Count, "file") + (files.Count >= 500 ? " (largest 500)" : "");
        }
        return new RowView(
            Chain.RowPosKey(letter, index),
            RowKind.Files,
            letter,
            index,
            "(files)",
            dir.Length > 0 ? $"Files directly in {dir}" : "",
            sub,
            Format.Bytes(sum),
            Palette.Files,
            e.SegKey,
            Fill: null,
            // The folded "(small N)" files open as a small row, so every file can be reached.
            Layout(raw, sum, FilesRest),
            files is null ? "loading…" : files.Count == 0 ? "no loose files" : "",
            expanded,
            CloseOnly,
            Deleting: null);
    }

    /// <summary>
    /// One loose file opened from a files (or small) row: its name over its attributes, the file at full width, and
    /// the Recycle Bin / delete / close controls of a folder row. While a job deletes the file the row is its
    /// progress bar. Once the folder's list no longer has the file, the controller closes the row
    /// (<see cref="Model.ReconcileFileRows"/>).
    /// </summary>
    private static RowView FileRow(Model m, ChainEntry e, string letter, int index)
    {
        var name = Chain.FileName(e);
        var node = m.Nodes.GetValueOrDefault(e.NodeId);
        var files = m.Files.GetValueOrDefault(e.NodeId);
        var color = Palette.ColorFor(name);
        var row = new RowView(
            Chain.RowPosKey(letter, index),
            RowKind.File,
            letter,
            index,
            name,
            "",
            SubPlaceholder,
            "",
            color,
            e.SegKey,
            Fill: null,
            [],
            "loading…",
            Expanded: null,
            CloseOnly,
            Deleting: null);
        if (node is null || files is null)
        {
            return row;
        }
        var file = files.FirstOrDefault(f => f.Name == name);
        if (file is null)
        {
            return row with { Title = Paths.JoinPath(node.Path, name), Placeholder = "no longer here" };
        }

        var path = Paths.JoinPath(node.Path, file.Name);
        var attrs = AttrsText(file.Attrs);
        var job = Jobs.ActiveJobFor(m.Jobs, path);
        DeletingView? deleting = null;
        if (job is not null)
        {
            var p = Jobs.Progress(job);
            deleting = new DeletingView(job.Id, p.Fraction, p.Text);
        }
        return row with
        {
            Title = path,
            Sub = string.Join(" · ", NonEmpty("file", attrs)),
            Size = Format.Bytes(file.Size),
            Segs =
            [
                new SegView("file", file.Name, 0, 100, color, SegStyle.Plain, Expandable: false, Progress: null,
                    new HoverInfo(path, NonEmpty(Format.Bytes(file.Size), attrs))),
            ],
            Placeholder = "",
            Controls = job is not null ? [] : DirControls,
            Deleting = deleting,
        };
    }
}
