using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace DeleteMan.Engine;

/// <summary>
/// Breadth-first scan of one drive (or, for tests, of any directory).
/// </summary>
/// <remarks>
/// Each drive gets an append-only arena of <see cref="DirNode"/>s (one per directory; files are only aggregated into
/// their parent). The scan proceeds level by level: all directories at depth <c>d</c> are listed in parallel before
/// any at depth <c>d+1</c>, so the shallow picture appears first and refines as deeper levels finish. Sizes propagate
/// up the parent chain with interlocked operations as each directory is listed, so every node's total is a live lower
/// bound until the drive's status is <see cref="ScanState.Done"/>.
/// </remarks>
public sealed class DriveScan
{
    /// <summary>Parent id of the root node.</summary>
    public const uint NoParent = uint.MaxValue;

    /// <summary>
    /// Stack size of the scan threads. Scanning is iterative (the frontier is a plain list, not a recursive call
    /// chain), so a small stack is plenty, which keeps the cost low even when several drives scan at once, each with
    /// its own pool.
    /// </summary>
    private const int ScanStackSize = 1 << 20;

    [ThreadStatic]
    private static List<uint>? t_children;

    private readonly Arena<DirNode> _arena = new();
    private readonly Stopwatch _started = Stopwatch.StartNew();
    private int _status;
    private int _depth;
    private long _dirsScanned;
    /// <summary>Bytes attributed to files found so far.</summary>
    private long _accounted;
    private int _cancel;
    private int _activeScans;
    private long _finishedMs;

    /// <summary>A scan rooted at <c>X:\</c>.</summary>
    public DriveScan(char letter)
        : this(letter, $"{letter}:")
    {
    }

    private DriveScan(char letter, string rootName)
    {
        Letter = letter;
        _arena.Push(new DirNode(rootName, NoParent, 0));
    }

    /// <summary>Drive letter the scan's node ids use.</summary>
    public char Letter { get; }

    /// <summary>Overall state.</summary>
    public ScanState Status => (ScanState)Volatile.Read(ref _status);

    /// <summary>Bytes attributed to files found so far.</summary>
    public ulong Accounted => (ulong)Interlocked.Read(ref _accounted);

    /// <summary>Deepest level reached.</summary>
    public uint Depth => (uint)Volatile.Read(ref _depth);

    /// <summary>Directories listed so far.</summary>
    public ulong DirsScanned => (ulong)Interlocked.Read(ref _dirsScanned);

    private bool IsCancelled => Volatile.Read(ref _cancel) != 0;

    /// <summary>A scan rooted at an arbitrary directory (used by tests); <paramref name="rootDisplayPath"/> is the path without <c>\\?\</c>.</summary>
    public static DriveScan WithRoot(char letter, string rootDisplayPath) =>
        new(letter, WidePath.FromDisplay(rootDisplayPath).Display);

    /// <summary>Parses <c>"C:123"</c> into <c>('C', 123)</c>; <see langword="null"/> if malformed.</summary>
    public static NodeRef? ParseNodeId(string id)
    {
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        if (colon != 1)
        {
            return null; // the part before ':' must be exactly one character
        }
        var c = char.ToUpperInvariant(id[0]);
        if (!char.IsAsciiLetter(c)
            || !uint.TryParse(id.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }
        return new NodeRef(c, index);
    }

    /// <summary>The node with id <paramref name="id"/>, or <see langword="null"/> if unknown.</summary>
    public DirNode? Node(uint id) => id > int.MaxValue ? null : _arena.TryGet((int)id);

    /// <summary>The UI id of a node, e.g. <c>"C:42"</c>.</summary>
    public string NodeId(uint index) => $"{Letter}:{index.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Full <c>\\?\</c> path of a node, rebuilt from the parent chain.</summary>
    public WidePath PathOf(uint id)
    {
        // The same string WidePath.FromComponents builds from the names root-down, written in one allocation:
        // the scan asks for a path for every directory it lists, and the list and builder it used to go through
        // were a good part of its garbage.
        var length = WidePrefix.Length;
        var parts = 0;
        for (var cur = id; ;)
        {
            var n = At(cur);
            length += n.Name.Length;
            parts++;
            if (n.Parent == NoParent)
            {
                break;
            }
            if (!EndsWithBackslash(At(n.Parent).Name))
            {
                length++; // separator before this part
            }
            cur = n.Parent;
        }
        var volumeRoot = parts == 1 && WidePath.IsBareDriveName(At(id).Name);
        if (volumeRoot)
        {
            length++;
        }
        return WidePath.FromWide(string.Create(length, (Scan: this, Id: id, VolumeRoot: volumeRoot),
            static (span, s) => s.Scan.WritePath(span, s.Id, s.VolumeRoot)));
    }

    private const string WidePrefix = @"\\?\";

    private static bool EndsWithBackslash(string s) => s.Length > 0 && s[^1] == '\\';

    /// <summary>Fills <paramref name="span"/> back to front with the path of <paramref name="id"/> (see <see cref="PathOf"/>).</summary>
    private void WritePath(Span<char> span, uint id, bool volumeRoot)
    {
        WidePrefix.CopyTo(span);
        var pos = span.Length;
        if (volumeRoot)
        {
            span[--pos] = '\\';
        }
        for (var cur = id; ;)
        {
            var n = At(cur);
            pos -= n.Name.Length;
            n.Name.CopyTo(span[pos..]);
            if (n.Parent == NoParent)
            {
                break;
            }
            if (!EndsWithBackslash(At(n.Parent).Name))
            {
                span[--pos] = '\\';
            }
            cur = n.Parent;
        }
        Debug.Assert(pos == WidePrefix.Length, "path length miscounted");
    }

    /// <summary>The folder name of a node; the full path for the root.</summary>
    public string DisplayName(uint id)
    {
        var n = At(id);
        return n.Parent == NoParent ? PathOf(id).Display : n.Name;
    }

    /// <summary>Asks every running pass to stop; the drive then ends as <see cref="ScanState.Cancelled"/>.</summary>
    public void Cancel() => Volatile.Write(ref _cancel, 1);

    /// <summary>
    /// Runs the level-synchronous BFS from <paramref name="start"/>, blocking until it finishes.
    /// </summary>
    /// <remarks>
    /// Each call gets its own dedicated thread pool, so scanning one drive (or rescanning a subtree) uses threads that
    /// no other scan shares. Two drive scans therefore make independent progress; a second scan can never starve a
    /// first by monopolising a common pool of blocking-I/O worker threads.
    /// </remarks>
    public void RunBfs(Gate gate, uint start)
    {
        BeginBfs();
        CompleteBfs(gate, start);
    }

    /// <summary>
    /// Marks the drive as scanning before the pass actually starts, so observers never see a stale
    /// <see cref="ScanState.Done"/> between a reset and its rescan. Must be paired with <see cref="CompleteBfs"/>.
    /// </summary>
    internal void BeginBfs()
    {
        Interlocked.Increment(ref _activeScans);
        Volatile.Write(ref _status, (int)ScanState.Scanning);
    }

    /// <summary>Runs the pass announced by <see cref="BeginBfs"/> and settles the drive status when the last pass ends.</summary>
    internal void CompleteBfs(Gate gate, uint start)
    {
        try
        {
            ScanFrom(gate, start);
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeScans) == 0)
            {
                var final = IsCancelled ? ScanState.Cancelled : ScanState.Done;
                Interlocked.Exchange(ref _finishedMs, _started.ElapsedMilliseconds);
                Volatile.Write(ref _status, (int)final);
            }
        }
    }

    /// <summary>Removes a node whose directory is gone from disk: ancestors lose its totals, the parent drops the child.</summary>
    public void RemoveNode(uint id)
    {
        var node = At(id);
        if (node.State == NodeState.Deleted)
        {
            return;
        }
        var bytes = (long)node.TotalBytes;
        var files = (long)node.TotalFiles;
        var dirs = (long)node.TotalDirs;
        MarkSubtreeDeleted(id);
        if (node.Parent != NoParent)
        {
            Propagate(node.Parent, -bytes, -files, -(dirs + 1));
            At(node.Parent).RemoveChild(id);
        }
        DirNode.AddSigned(ref _accounted, -bytes);
    }

    /// <summary>
    /// Detaches a node's subtree and zeroes its counters so <see cref="RunBfs"/> can re-list it. The caller must have
    /// paused the gate so no listing is in flight.
    /// </summary>
    public void ResetSubtree(uint id)
    {
        var node = At(id);
        var bytes = (long)node.TotalBytes;
        var files = (long)node.TotalFiles;
        var dirs = (long)node.TotalDirs;
        foreach (var k in node.TakeChildren())
        {
            MarkSubtreeDeleted(k);
        }
        Propagate(id, -bytes, -files, -dirs);
        node.SetOwn(0, 0);
        node.ErrorCode = 0;
        node.State = NodeState.Pending;
        DirNode.AddSigned(ref _accounted, -bytes);
    }

    /// <summary>Subtracts one file's size from a node's own and recursive totals (after a single-file deletion).</summary>
    public void SubtractFile(uint id, ulong size)
    {
        var node = At(id);
        node.AddOwn(-(long)size, -1);
        Propagate(id, -(long)size, -1, 0);
        DirNode.AddSigned(ref _accounted, -(long)size);
    }

    /// <summary>
    /// Resolves a directory path (components below the scan root, e.g. <c>["Users", "steve"]</c>) to the deepest
    /// matching live node, case-insensitively. If the full path isn't in the tree (a middle directory is new or
    /// unscanned), returns the deepest existing ancestor, whose refresh will then pick up the new child.
    /// </summary>
    public uint FindNode(IReadOnlyList<string> components)
    {
        var cur = 0u;
        foreach (var comp in components)
        {
            var node = At(cur);
            if (node.State == NodeState.Deleted)
            {
                return cur;
            }
            var found = false;
            foreach (var c in node.ChildrenSnapshot())
            {
                var cn = Node(c);
                if (cn is not null && cn.State != NodeState.Deleted && string.Equals(cn.Name, comp, StringComparison.OrdinalIgnoreCase))
                {
                    cur = c;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                break; // component not present; `cur` is the deepest existing ancestor
            }
        }
        return cur;
    }

    /// <summary>
    /// Re-reads one directory after an external filesystem change and reconciles the tree.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Loose files added/removed/resized (same set of subdirectories): update own size/count and propagate the
    /// delta up the parent chain (cheap).</item>
    /// <item>A subdirectory added: create its node and scan its subtree; removed: drop its node.</item>
    /// </list>
    /// Only valid once the drive's initial scan is <see cref="ScanState.Done"/>, and the caller must ensure no deletion
    /// is running (the deleter maintains the tree itself). Safe to call on a stale id: a gone/deleted directory is
    /// left for its parent's refresh to reconcile.
    /// </remarks>
    public void RefreshDir(Gate gate, uint id)
    {
        var node = Node(id);
        if (node is null || node.State == NodeState.Deleted)
        {
            return;
        }
        var path = PathOf(id);
        ulong newOwnBytes = 0;
        ulong newOwnFiles = 0;
        var diskDirs = new List<string>();
        var error = WinFs.TryListDir(path, (name, attrs, size) =>
        {
            if (WinFs.IsReparse(attrs))
            {
                return;
            }
            if (WinFs.IsDir(attrs))
            {
                diskDirs.Add(name.ToString());
            }
            else
            {
                newOwnFiles++;
                if (!WinFs.IsPlaceholder(attrs))
                {
                    newOwnBytes += size;
                }
            }
        });
        if (error != 0)
        {
            return; // directory vanished or unreadable; the parent's refresh reconciles its removal
        }
        node.State = NodeState.Done;

        var curChildren = node.ChildrenSnapshot();
        var curNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in curChildren)
        {
            var cn = Node(c);
            if (cn is not null && cn.State != NodeState.Deleted)
            {
                curNames.Add(cn.Name);
            }
        }
        var diskNames = new HashSet<string>(diskDirs, StringComparer.OrdinalIgnoreCase);

        // Own (loose-file) delta always applies.
        var oldBytes = node.OwnBytes;
        var oldFiles = node.OwnFiles;
        if (oldBytes != newOwnBytes || oldFiles != newOwnFiles)
        {
            node.SetOwn(newOwnBytes, newOwnFiles);
            var db = (long)newOwnBytes - (long)oldBytes;
            Propagate(id, db, (long)newOwnFiles - (long)oldFiles, 0);
            DirNode.AddSigned(ref _accounted, db);
        }
        if (curNames.SetEquals(diskNames))
        {
            return;
        }

        // Structural change: drop removed subdirectories and scan newly appeared ones.
        foreach (var c in curChildren)
        {
            var cn = Node(c);
            if (cn is not null && cn.State != NodeState.Deleted && !diskNames.Contains(cn.Name))
            {
                RemoveNode(c);
            }
        }
        var childDepth = ChildDepth(node);
        foreach (var name in diskDirs)
        {
            if (curNames.Contains(name))
            {
                continue;
            }
            var idx = (uint)_arena.Push(new DirNode(name, id, childDepth));
            node.AddChild(idx);
            Propagate(id, 0, 0, 1); // the new subdirectory itself
            ScanFrom(gate, idx);    // its contents
        }
    }

    /// <summary>Progress snapshot for the UI.</summary>
    public ScanStatus StatusView()
    {
        var status = Status;
        var elapsed = status is ScanState.Done or ScanState.Cancelled
            ? (ulong)Interlocked.Read(ref _finishedMs)
            : (ulong)_started.ElapsedMilliseconds;
        return new ScanStatus(Letter.ToString(), status, Depth, DirsScanned, Accounted, elapsed);
    }

    /// <summary>Snapshot of a node for the UI; <see langword="null"/> if the id is unknown or the node was removed.</summary>
    /// <param name="id">Node id.</param>
    /// <param name="maxChildren">Largest subfolders to list individually; the rest are folded into the "other" totals.</param>
    public NodeView? NodeView(uint id, int maxChildren)
    {
        var node = Node(id);
        if (node is null || node.State == NodeState.Deleted)
        {
            return null;
        }
        var kids = new List<(uint Id, DirNode Node, ulong Bytes)>();
        foreach (var c in node.ChildrenSnapshot())
        {
            var n = Node(c);
            if (n is not null && n.State != NodeState.Deleted)
            {
                kids.Add((c, n, n.TotalBytes));
            }
        }
        // Stable sort, largest first (ties keep listing order, like Rust's sort_by).
        var sorted = kids.OrderByDescending(k => k.Bytes).ToList();
        var tailStart = Math.Min(Math.Max(maxChildren, 0), sorted.Count);
        ulong otherCount = 0;
        ulong otherBytes = 0;
        for (var i = tailStart; i < sorted.Count; i++)
        {
            otherCount++;
            otherBytes += sorted[i].Bytes;
        }
        var children = new List<ChildView>(tailStart);
        for (var i = 0; i < tailStart; i++)
        {
            var (c, n, bytes) = sorted[i];
            children.Add(new ChildView(NodeId(c), n.Name, bytes, n.TotalFiles, n.State, ErrorCodeOf(n)));
        }
        var job = node.DeletingJob;
        return new NodeView(
            NodeId(id),
            DisplayName(id),
            PathOf(id).Display,
            node.Parent == NoParent,
            node.TotalBytes,
            node.TotalFiles,
            node.TotalDirs,
            node.OwnBytes,
            node.OwnFiles,
            node.State,
            ErrorCodeOf(node),
            children,
            otherCount,
            otherBytes,
            job == 0 ? null : job.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Re-reads one directory and returns its loose files, largest first, capped at <paramref name="cap"/>.</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The directory cannot be listed.</exception>
    public IReadOnlyList<FileView> ListFiles(uint id, int cap)
    {
        var path = PathOf(id);
        var files = new List<FileView>();
        WinFs.ListDir(path, (name, attrs, size) =>
        {
            if (WinFs.IsDir(attrs))
            {
                return;
            }
            var local = WinFs.IsReparse(attrs) || WinFs.IsPlaceholder(attrs) ? 0 : size;
            files.Add(new FileView(name.ToString(), local, attrs));
        });
        files.Sort((a, b) =>
        {
            var bySize = b.Size.CompareTo(a.Size);
            return bySize != 0 ? bySize : string.CompareOrdinal(a.Name, b.Name);
        });
        if (files.Count > cap)
        {
            files.RemoveRange(Math.Max(cap, 0), files.Count - Math.Max(cap, 0));
        }
        return files;
    }

    private static uint? ErrorCodeOf(DirNode n) => n.State == NodeState.Error ? n.ErrorCode : null;

    private static ushort ChildDepth(DirNode n) => n.Depth == ushort.MaxValue ? ushort.MaxValue : (ushort)(n.Depth + 1);

    private DirNode At(uint id) => _arena[(int)id];

    private void Propagate(uint from, long bytes, long files, long dirs)
    {
        var cur = from;
        while (true)
        {
            var n = At(cur);
            n.AddTotals(bytes, files, dirs);
            if (n.Parent == NoParent)
            {
                break;
            }
            cur = n.Parent;
        }
    }

    private void MarkSubtreeDeleted(uint id)
    {
        var stack = new Stack<uint>();
        stack.Push(id);
        while (stack.TryPop(out var cur))
        {
            var n = At(cur);
            n.State = NodeState.Deleted;
            foreach (var c in n.ChildrenSnapshot())
            {
                stack.Push(c);
            }
        }
    }

    /// <summary>
    /// The level-synchronous BFS itself, on its own pool, without touching the drive's scan status. Used by
    /// <see cref="RunBfs"/> (which wraps it with status) and by <see cref="RefreshDir"/> to scan a newly appeared
    /// subtree without flipping the whole drive back to "scanning".
    /// </summary>
    private void ScanFrom(Gate gate, uint start)
    {
        var level = (uint)At(start).Depth;
        using var pool = new DedicatedThreadPool("scan", DeletePool.DefaultThreadCount(), ScanStackSize);
        var options = new ParallelOptions { TaskScheduler = pool, MaxDegreeOfParallelism = pool.MaximumConcurrencyLevel };
        List<uint> frontier = [start];
        while (frontier.Count > 0 && !IsCancelled)
        {
            var next = new List<uint>();
            var nextLock = new Lock();
            // One directory at a time per worker: listing times vary wildly, so static chunking would idle threads.
            Parallel.ForEach(
                Partitioner.Create(frontier, EnumerablePartitionerOptions.NoBuffering),
                options,
                () => new List<uint>(),
                (id, _, local) =>
                {
                    local.AddRange(ScanOne(gate, id));
                    return local;
                },
                local =>
                {
                    lock (nextLock)
                    {
                        next.AddRange(local);
                    }
                });
            frontier = next;
            level++;
            UpdateDepth(level);
        }
    }

    private void UpdateDepth(uint level)
    {
        while (true)
        {
            var cur = Volatile.Read(ref _depth);
            if ((uint)cur >= level || Interlocked.CompareExchange(ref _depth, (int)level, cur) == cur)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Lists one directory, creates child nodes, records own sizes and propagates them to the ancestors. Returns the
    /// ids of the subdirectories found (the next BFS level).
    /// </summary>
    private uint[] ScanOne(Gate gate, uint id)
    {
        var node = At(id);
        if (IsCancelled || node.State == NodeState.Deleted)
        {
            return [];
        }
        using var inflight = gate.Enter();
        if (IsCancelled || node.State == NodeState.Deleted)
        {
            return [];
        }
        node.State = NodeState.Scanning;

        var path = PathOf(id);
        var childDepth = ChildDepth(node);
        ulong ownBytes = 0;
        ulong ownFiles = 0;
        // One scratch list per scan thread; the node keeps its own copy (ToArray below).
        var children = t_children ??= new List<uint>(256);
        children.Clear();
        var error = WinFs.TryListDir(path, (name, attrs, size) =>
        {
            if (WinFs.IsReparse(attrs))
            {
                return; // junctions, symlinks, cloud placeholders: never followed, never counted
            }
            if (WinFs.IsDir(attrs))
            {
                children.Add((uint)_arena.Push(new DirNode(name.ToString(), id, childDepth)));
            }
            else
            {
                ownFiles++;
                if (!WinFs.IsPlaceholder(attrs))
                {
                    ownBytes += size;
                }
            }
        });

        var kids = children.ToArray();
        node.SetChildren(kids);
        node.SetOwn(ownBytes, ownFiles);
        if (error == 0)
        {
            node.State = NodeState.Done;
        }
        else
        {
            node.ErrorCode = (uint)error;
            node.State = NodeState.Error;
        }
        Propagate(id, (long)ownBytes, (long)ownFiles, kids.Length);
        Interlocked.Add(ref _accounted, (long)ownBytes);
        Interlocked.Increment(ref _dirsScanned);
        return kids;
    }
}
