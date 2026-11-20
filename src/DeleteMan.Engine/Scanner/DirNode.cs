using System.Runtime.CompilerServices;

namespace DeleteMan.Engine;

/// <summary>State of one directory node in a scan.</summary>
public enum NodeState
{
    /// <summary>Not listed yet.</summary>
    Pending,

    /// <summary>Being listed.</summary>
    Scanning,

    /// <summary>Listed.</summary>
    Done,

    /// <summary>Could not be listed (see <see cref="DirNode.ErrorCode"/>).</summary>
    Error,

    /// <summary>Removed from the tree (deleted on disk, or detached by a rescan). Never shown, never scanned.</summary>
    Deleted,
}

/// <summary>
/// One directory in a scan arena. Files are not nodes; they are only aggregated into their parent. Counters are
/// updated with interlocked operations while the scan runs, so readers always see a live lower bound.
/// </summary>
public sealed class DirNode
{
    private long _ownBytes;
    private long _ownFiles;
    private long _totalBytes;
    private long _totalFiles;
    private long _totalDirs;
    private int _state;
    private int _errorCode;
    private long _deletingJob;

    /// <summary>
    /// Child ids. Copy-on-write: readers take the current array without locking; writers replace it under the node's
    /// lock, so a snapshot never changes underneath its reader.
    /// </summary>
    private uint[] _children = [];

    /// <summary>
    /// Child-list writers take one of these, chosen by the node's identity. Writes are rare (a node's children are
    /// set once by the scan and touched again only by deletions and rescans), so a few shared locks see no
    /// contention, and the tree carries one object per directory less. Every object in the tree is walked by each
    /// garbage collection, and those collections pause the UI thread, so the object count matters.
    /// </summary>
    private static readonly Lock[] ChildrenLocks = CreateLocks(64);

    private Lock ChildrenLock => ChildrenLocks[RuntimeHelpers.GetHashCode(this) & (ChildrenLocks.Length - 1)];

    private static Lock[] CreateLocks(int count)
    {
        var locks = new Lock[count];
        for (var i = 0; i < locks.Length; i++)
        {
            locks[i] = new Lock();
        }
        return locks;
    }

    internal DirNode(string name, uint parent, ushort depth)
    {
        Name = name;
        Parent = parent;
        Depth = depth;
    }

    /// <summary>Entry name (exact). For the root node this is the root path without the <c>\\?\</c> prefix.</summary>
    public string Name { get; }

    /// <summary>Parent id, or <see cref="DriveScan.NoParent"/> for the root.</summary>
    public uint Parent { get; }

    /// <summary>Depth below the scan root (the root is 0).</summary>
    public ushort Depth { get; }

    /// <summary>Bytes of the files directly inside this directory.</summary>
    public ulong OwnBytes => (ulong)Interlocked.Read(ref _ownBytes);

    /// <summary>Number of files directly inside this directory.</summary>
    public ulong OwnFiles => (ulong)Interlocked.Read(ref _ownFiles);

    /// <summary>Recursive bytes (including own).</summary>
    public ulong TotalBytes => (ulong)Interlocked.Read(ref _totalBytes);

    /// <summary>Recursive file count (including own).</summary>
    public ulong TotalFiles => (ulong)Interlocked.Read(ref _totalFiles);

    /// <summary>Recursive subdirectory count.</summary>
    public ulong TotalDirs => (ulong)Interlocked.Read(ref _totalDirs);

    /// <summary>Current state.</summary>
    public NodeState State
    {
        get => (NodeState)Volatile.Read(ref _state);
        internal set => Volatile.Write(ref _state, (int)value);
    }

    /// <summary>Win32 error code of the failed listing (meaningful when <see cref="State"/> is <see cref="NodeState.Error"/>).</summary>
    public uint ErrorCode
    {
        get => (uint)Volatile.Read(ref _errorCode);
        internal set => Volatile.Write(ref _errorCode, (int)value);
    }

    /// <summary>Id of the deletion job targeting this node, 0 when none.</summary>
    public ulong DeletingJob
    {
        get => (ulong)Interlocked.Read(ref _deletingJob);
        internal set => Interlocked.Exchange(ref _deletingJob, (long)value);
    }

    /// <summary>The current child ids (an immutable snapshot).</summary>
    public IReadOnlyList<uint> ChildrenSnapshot() => Volatile.Read(ref _children);

    internal void SetChildren(uint[] children)
    {
        lock (ChildrenLock)
        {
            Volatile.Write(ref _children, children);
        }
    }

    /// <summary>Detaches every child and returns them.</summary>
    internal uint[] TakeChildren()
    {
        lock (ChildrenLock)
        {
            var kids = _children;
            Volatile.Write(ref _children, []);
            return kids;
        }
    }

    internal void AddChild(uint id)
    {
        lock (ChildrenLock)
        {
            Volatile.Write(ref _children, [.. _children, id]);
        }
    }

    internal void RemoveChild(uint id)
    {
        lock (ChildrenLock)
        {
            Volatile.Write(ref _children, Array.FindAll(_children, c => c != id));
        }
    }

    internal void SetOwn(ulong bytes, ulong files)
    {
        Interlocked.Exchange(ref _ownBytes, (long)bytes);
        Interlocked.Exchange(ref _ownFiles, (long)files);
    }

    internal void AddOwn(long bytes, long files)
    {
        AddSigned(ref _ownBytes, bytes);
        AddSigned(ref _ownFiles, files);
    }

    internal void AddTotals(long bytes, long files, long dirs)
    {
        AddSigned(ref _totalBytes, bytes);
        AddSigned(ref _totalFiles, files);
        AddSigned(ref _totalDirs, dirs);
    }

    /// <summary>Adds a signed delta to an unsigned counter, saturating at zero instead of wrapping.</summary>
    internal static void AddSigned(ref long counter, long delta)
    {
        if (delta >= 0)
        {
            Interlocked.Add(ref counter, delta);
            return;
        }
        var magnitude = (ulong)-delta;
        while (true)
        {
            var current = Interlocked.Read(ref counter);
            var cur = (ulong)current;
            var next = cur > magnitude ? cur - magnitude : 0;
            if (Interlocked.CompareExchange(ref counter, (long)next, current) == current)
            {
                return;
            }
        }
    }
}
