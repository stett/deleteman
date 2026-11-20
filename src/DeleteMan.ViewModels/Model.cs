// UI state and poll bookkeeping (port of model.ts). Nothing in here touches the UI or the engine directly.

// Implicit usings bring System.IO.DriveInfo into scope; the engine's record is the one meant everywhere here.
global using DriveInfo = DeleteMan.Engine.DriveInfo;

using DeleteMan.Engine;

namespace DeleteMan.ViewModels;

public sealed record HoverKey(string Row, string Seg);

/// <summary>Segments built for one node, with the inputs they were built from (see <see cref="Model.SegCache"/>).</summary>
internal sealed record SegCacheEntry(NodeView Node, IReadOnlyList<JobView> Jobs, IReadOnlyList<FileView>? Files, Rows.RawSeg[] Segs);

public sealed class Model
{
    public IReadOnlyList<DriveInfo> Drives { get; set; } = [];

    /// <summary>Per drive letter: the expanded rows below the drive row, top to bottom. Empty = drive collapsed.</summary>
    public OrderedDictionary<string, IReadOnlyList<ChainEntry>> Chains { get; } = [];

    public Dictionary<string, NodeView> Nodes { get; } = [];

    public Dictionary<string, IReadOnlyList<FileView>> Files { get; } = [];

    /// <summary>Signature of the data a files row was last fetched for; a change triggers a re-fetch.</summary>
    public Dictionary<string, string> FilesSig { get; } = [];

    public Dictionary<string, ScanStatus> Scans { get; } = [];

    public IReadOnlyList<JobView> Jobs { get; set; } = [];

    public HoverKey? Hover { get; set; }

    /// <summary>
    /// Per node id: the segments <see cref="Rows"/> last built for it, with the node view and job list they were
    /// built from. Valid while both are still the same objects (see <see cref="ApplyPoll"/>).
    /// </summary>
    internal Dictionary<string, SegCacheEntry> SegCache { get; } = [];

    /// <summary>
    /// The nodes to poll, one per node with a row open, each with the subfolders its rows need listed:
    /// <see cref="Chain.MaxChildren"/> for the folder's own row and as many again for every page of its leftover
    /// ("(other N)") open below it, so each page has the next folders to show.
    /// </summary>
    public IReadOnlyList<NodeRequest> NodeRequests()
    {
        var pages = new OrderedDictionary<string, int>();
        foreach (var chain in Chains.Values)
        {
            foreach (var e in chain)
            {
                pages[e.NodeId] = pages.GetValueOrDefault(e.NodeId) + (e.Kind == EntryKind.Other ? 1 : 0);
            }
        }
        return pages.Select(kv => new NodeRequest(kv.Key, Chain.MaxChildren * (1 + kv.Value))).ToList();
    }

    public IReadOnlyList<string> ExpandedNodeIds() => NodeRequests().Select(r => r.Id).ToList();

    public IReadOnlyList<string> LettersWithUsedRow() =>
        Chains.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();

    public void ApplyPoll(
        IReadOnlyList<DriveInfo> drives,
        IReadOnlyList<JobView> jobs,
        IReadOnlyList<NodeView> nodes,
        IReadOnlyList<string> requested,
        IReadOnlyList<ScanStatus> scans)
    {
        // A poll brings fresh objects every 250 ms whether or not anything changed. Keep the previous object when
        // the content is the same: the row views built from it then come out equal, so the UI touches nothing,
        // and the segment cache (keyed on these references) stays warm. During a scan most nodes do change each
        // poll; after it, almost none do.
        if (!Seq.Equal(Drives, drives))
        {
            Drives = drives;
        }
        if (!SameJobs(Jobs, jobs))
        {
            Jobs = jobs;
        }
        foreach (var s in scans)
        {
            if (!Scans.TryGetValue(s.Drive, out var oldScan) || oldScan != s)
            {
                Scans[s.Drive] = s;
            }
        }

        var live = nodes.Where(n => n.State != NodeState.Deleted).ToList();
        var present = live.Select(n => n.Id).ToHashSet();
        var requestedSet = requested.ToHashSet();
        foreach (var n in live)
        {
            if (!Nodes.TryGetValue(n.Id, out var old) || !SameContent(old, n))
            {
                Nodes[n.Id] = n;
            }
        }
        foreach (var id in requestedSet)
        {
            if (!present.Contains(id))
            {
                Nodes.Remove(id);
                SegCache.Remove(id);
            }
        }

        var letters = drives.Select(d => d.Letter).ToHashSet();
        foreach (var (letter, chain) in Chains.ToList())
        {
            if (!letters.Contains(letter))
            {
                Chains.Remove(letter);
            }
            else
            {
                Chains[letter] = Chain.PruneMissing(chain, requestedSet, present);
            }
        }
    }

    /// <summary>True when two node views describe the same state (records compare their child lists by reference).</summary>
    public static bool SameContent(NodeView a, NodeView b) =>
        ReferenceEquals(a, b) || (a.Equals(b with { Children = a.Children }) && Seq.Equal(a.Children, b.Children));

    /// <summary>True when two job lists describe the same state (a job's error list compares by reference).</summary>
    public static bool SameJobs(IReadOnlyList<JobView> a, IReadOnlyList<JobView> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i])
                && (!a[i].Equals(b[i] with { Errors = a[i].Errors }) || !Seq.Equal(a[i].Errors, b[i].Errors)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Node ids whose loose-file list must be (re-)fetched: open files and file rows, and shown folder rows whose
    /// node has loose files, so a folder that amounts to one file can show it by name (<see cref="Rows.SingleFile"/>).
    /// Also drops the lists of rows no longer shown.
    /// </summary>
    public IReadOnlyList<string> StaleFileLists()
    {
        var open = new List<string>();
        foreach (var chain in Chains.Values)
        {
            foreach (var e in chain)
            {
                var wanted = e.Kind switch
                {
                    EntryKind.Files or EntryKind.File => true,
                    EntryKind.Used or EntryKind.Dir => Nodes.TryGetValue(e.NodeId, out var node) && node.OwnFiles > 0,
                    _ => false,
                };
                if (wanted && !open.Contains(e.NodeId))
                {
                    open.Add(e.NodeId);
                }
            }
        }
        foreach (var id in Files.Keys.ToList())
        {
            if (!open.Contains(id))
            {
                Files.Remove(id);
            }
        }
        foreach (var id in FilesSig.Keys.ToList())
        {
            if (!open.Contains(id))
            {
                FilesSig.Remove(id);
            }
        }

        var finished = string.Join(",", Jobs.Where(j => !ViewModels.Jobs.IsActive(j)).Select(j => j.Id));
        var stale = new List<string>();
        foreach (var id in open)
        {
            var sig = (Nodes.TryGetValue(id, out var node) ? $"{node.OwnFiles}:{node.OwnBytes}" : "?") + "|" + finished;
            if (!FilesSig.TryGetValue(id, out var old) || old != sig)
            {
                FilesSig[id] = sig;
                stale.Add(id);
            }
        }
        return stale;
    }

    /// <summary>
    /// Keeps file rows in step with their folders' fetched lists: a file row whose file is gone (deleted, or out of
    /// the largest 500) closes with everything below it, and a files row whose folder amounts to one file
    /// (<see cref="Rows.SingleFile"/>) becomes that file's row, so the file is one click away whether its list
    /// arrived before or after the click. Folders whose list has not arrived are left alone. True when a chain
    /// changed.
    /// </summary>
    public bool ReconcileFileRows()
    {
        var changed = false;
        foreach (var (letter, chain) in Chains.ToList())
        {
            var next = Reconcile(chain);
            if (!ReferenceEquals(next, chain))
            {
                Chains[letter] = next;
                changed = true;
            }
        }
        return changed;
    }

    private IReadOnlyList<ChainEntry> Reconcile(IReadOnlyList<ChainEntry> chain)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            var e = chain[i];
            if (!Files.TryGetValue(e.NodeId, out var files))
            {
                continue;
            }
            if (e.Kind == EntryKind.File && !files.Any(f => f.Name == Chain.FileName(e)))
            {
                return chain.Take(i).ToList();
            }
            if (e.Kind == EntryKind.Files && Rows.SingleFile(files) is { } single)
            {
                // The file's row stands in for the files row and whatever was open below it.
                var head = chain.Take(i).ToList();
                head.Add(new ChainEntry(EntryKind.File, e.NodeId, Chain.FileKeyPrefix + single.Name, Auto: e.Auto));
                return head;
            }
        }
        return chain;
    }
}
