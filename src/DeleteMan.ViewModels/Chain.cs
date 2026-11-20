namespace DeleteMan.ViewModels;

/// <summary>
/// <c>Other</c> is the leftover of a used/dir row ("(small N)" or "(other N)": the subfolders too small to draw and
/// the ones the engine did not list), opened as a page of them, largest first; a page's own leftover opens the
/// next page, another <c>Other</c> entry of the same node. <c>Small</c> is the "(small N)" group of a files row.
/// <c>File</c> is one loose file of a files (or small) row, opened as its own row so it can be deleted with the row
/// controls.
/// </summary>
public enum EntryKind
{
    Used,
    Dir,
    Files,
    Small,
    File,
    Other,
}

/// <summary>
/// One expanded row below a drive. <paramref name="SegKey"/> is the segment of the row above that opened it; for
/// files, small, file and other rows <paramref name="NodeId"/> is the folder node they belong to (a file row's
/// <paramref name="SegKey"/> is <c>f:</c> plus its name). <paramref name="Pinned"/> rows keep the largest folder
/// open below them (<see cref="Pins"/>); <paramref name="Auto"/> rows were opened by a pin rather than by the user,
/// so a pin may replace them. <paramref name="DrivePin"/> is set on the used row only: the drive row's own pin is
/// on, a pin one level above the chain that owns the used row and everything below it, so it lives exactly as long
/// as the drive is open.
/// </summary>
public sealed record ChainEntry(EntryKind Kind, string NodeId, string SegKey, bool Pinned = false, bool Auto = false, bool DrivePin = false);

/// <summary>Pure chain transitions.</summary>
public static class Chain
{
    /// <summary>
    /// Subfolders a folder row draws from (its largest), and the size of each page of its leftover below it. The
    /// engine is asked for this many per open page of a node (<see cref="Model.NodeRequests"/>).
    /// </summary>
    public const int MaxChildren = 64;

    /// <summary>Segment key prefix of a loose file in a files row; the rest of the key is the file name.</summary>
    public const string FileKeyPrefix = "f:";

    /// <summary>Rows are keyed by position in a drive's chain (letter + index); the drive row is index -1.</summary>
    public static string RowPosKey(string letter, int index) => $"{letter}#{index}";

    public static ChainEntry UsedEntry(string rootId) => new(EntryKind.Used, rootId, "used");

    public static bool IsFileKey(string segKey) => segKey.StartsWith(FileKeyPrefix, StringComparison.Ordinal);

    /// <summary>The name of the file a file entry (or file segment key) stands for.</summary>
    public static string FileName(ChainEntry e) => e.SegKey[FileKeyPrefix.Length..];

    /// <summary>The row that segment <paramref name="segKey"/> of <paramref name="parent"/>'s row opens.</summary>
    public static ChainEntry EntryFor(ChainEntry parent, string segKey) => segKey switch
    {
        "files" => new ChainEntry(EntryKind.Files, parent.NodeId, segKey),
        "small" => new ChainEntry(EntryKind.Small, parent.NodeId, segKey),
        "other" => new ChainEntry(EntryKind.Other, parent.NodeId, segKey),
        _ when IsFileKey(segKey) => new ChainEntry(EntryKind.File, parent.NodeId, segKey),
        _ => new ChainEntry(EntryKind.Dir, segKey, segKey),
    };

    /// <summary>
    /// Expands <paramref name="entry"/> below row <paramref name="index"/>, replacing any deeper chain; clicking the
    /// segment that is already expanded collapses it instead.
    /// </summary>
    public static IReadOnlyList<ChainEntry> ExpandAt(IReadOnlyList<ChainEntry> chain, int index, ChainEntry entry)
    {
        var head = chain.Take(index + 1).ToList();
        var current = index + 1 < chain.Count ? chain[index + 1] : null;
        if (current is not null && current.SegKey == entry.SegKey)
        {
            return head;
        }
        head.Add(entry);
        return head;
    }

    /// <summary>Closes row <paramref name="index"/> and everything below it.</summary>
    public static IReadOnlyList<ChainEntry> CloseAt(IReadOnlyList<ChainEntry> chain, int index) =>
        chain.Take(Math.Max(0, index)).ToList();

    /// <summary>Cuts the chain at the first row whose node was requested but not returned (deleted / unknown).</summary>
    public static IReadOnlyList<ChainEntry> PruneMissing(
        IReadOnlyList<ChainEntry> chain,
        IReadOnlySet<string> requested,
        IReadOnlySet<string> present)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            var id = chain[i].NodeId;
            if (requested.Contains(id) && !present.Contains(id))
            {
                return chain.Take(i).ToList();
            }
        }
        return chain;
    }
}
