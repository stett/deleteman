namespace DeleteMan.Engine;

/// <summary>Overall state of a drive scan.</summary>
public enum ScanState
{
    /// <summary>Not started (or the drive was never opened).</summary>
    Idle,

    /// <summary>At least one breadth-first pass is running.</summary>
    Scanning,

    /// <summary>Every pass finished.</summary>
    Done,

    /// <summary>The scan was cancelled.</summary>
    Cancelled,
}

/// <summary>Progress of a drive scan.</summary>
/// <param name="Drive">Drive letter, e.g. <c>"C"</c>.</param>
/// <param name="Status">Scan state.</param>
/// <param name="Depth">Deepest level reached.</param>
/// <param name="DirsScanned">Directories listed so far.</param>
/// <param name="AccountedBytes">Bytes attributed to files found so far.</param>
/// <param name="ElapsedMs">Time since the scan started (frozen once it finishes).</param>
public sealed record ScanStatus(string Drive, ScanState Status, uint Depth, ulong DirsScanned, ulong AccountedBytes, ulong ElapsedMs);

/// <summary>One node the UI wants in a poll: its id and how many of its largest subfolders to list.</summary>
/// <param name="Id">Node id, e.g. <c>"C:42"</c>.</param>
/// <param name="MaxChildren">Subfolders to list individually, largest first; the rest are folded into the "other" totals.</param>
public sealed record NodeRequest(string Id, int MaxChildren);

/// <summary>A subfolder row inside a <see cref="NodeView"/>.</summary>
/// <param name="Id">Node id, e.g. <c>"C:42"</c>.</param>
/// <param name="Name">Folder name.</param>
/// <param name="TotalBytes">Recursive bytes.</param>
/// <param name="TotalFiles">Recursive file count.</param>
/// <param name="State">Node state.</param>
/// <param name="ErrorCode">Win32 error code when the folder could not be listed.</param>
public sealed record ChildView(string Id, string Name, ulong TotalBytes, ulong TotalFiles, NodeState State, uint? ErrorCode);

/// <summary>Snapshot of one scan node for the UI.</summary>
/// <param name="Id">Node id, e.g. <c>"C:42"</c>.</param>
/// <param name="Name">Folder name (the full root path for the root node).</param>
/// <param name="Path">Full display path.</param>
/// <param name="IsRoot">True for the scan root.</param>
/// <param name="TotalBytes">Recursive bytes.</param>
/// <param name="TotalFiles">Recursive file count.</param>
/// <param name="TotalDirs">Recursive subdirectory count.</param>
/// <param name="OwnBytes">Bytes of the files directly inside.</param>
/// <param name="OwnFiles">Files directly inside.</param>
/// <param name="State">Node state.</param>
/// <param name="ErrorCode">Win32 error code when the folder could not be listed.</param>
/// <param name="Children">Direct subfolders, largest first, at most the requested number.</param>
/// <param name="OtherCount">Subfolders beyond the requested number, folded.</param>
/// <param name="OtherBytes">Total bytes of the folded subfolders.</param>
/// <param name="DeletingJobId">Id of the deletion job targeting this node, if any.</param>
public sealed record NodeView(
    string Id,
    string Name,
    string Path,
    bool IsRoot,
    ulong TotalBytes,
    ulong TotalFiles,
    ulong TotalDirs,
    ulong OwnBytes,
    ulong OwnFiles,
    NodeState State,
    uint? ErrorCode,
    IReadOnlyList<ChildView> Children,
    ulong OtherCount,
    ulong OtherBytes,
    string? DeletingJobId);

/// <summary>A loose file inside a folder.</summary>
/// <param name="Name">File name.</param>
/// <param name="Size">Size on the local disk (0 for reparse points and cloud placeholders).</param>
/// <param name="Attrs">Win32 file attributes.</param>
public sealed record FileView(string Name, ulong Size, uint Attrs);
