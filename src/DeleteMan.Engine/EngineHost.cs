using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DeleteMan.Engine;

/// <summary>What to delete: a scanned folder, or one loose file inside it.</summary>
/// <param name="NodeId">The folder's node id, e.g. <c>"C:42"</c>.</param>
/// <param name="FileName">A loose file inside that folder, or <see langword="null"/> to delete the folder itself.</param>
public sealed record DeleteTarget(string NodeId, string? FileName = null);

/// <summary>Everything the UI refreshes on one poll tick.</summary>
/// <param name="Drives">Mounted volumes (cached snapshot).</param>
/// <param name="Jobs">Every known deletion job.</param>
/// <param name="Nodes">Views of the requested nodes; unknown or deleted ids are omitted.</param>
/// <param name="Scans">Scan status of each requested drive letter, in request order.</param>
public sealed record PollResult(IReadOnlyList<DriveInfo> Drives, IReadOnlyList<JobView> Jobs, IReadOnlyList<NodeView> Nodes, IReadOnlyList<ScanStatus> Scans);

/// <summary>
/// Owns the per-drive scans, the deletion jobs, the journal and the filesystem watchers, and exposes the commands the
/// UI calls. Every rejected command throws <see cref="EngineException"/> with a message meant to be shown verbatim.
/// </summary>
/// <remarks>
/// All methods are thread-safe. Scans, deletions and rescans run on their own threads; commands only start them and
/// return. <see cref="ListFiles"/> lists a directory synchronously and the journal is written synchronously by the
/// commands that change the job list, so call those off the UI thread if a slow disk is a concern.
/// </remarks>
public sealed class EngineHost : IDisposable
{
    /// <summary>The number of subfolders the UI shows per expanded folder (<see cref="Poll"/>'s default).</summary>
    public const int DefaultMaxChildren = 64;

    /// <summary>
    /// The most subfolders a node lists in one view. The UI asks for one more window of 64 per open page of a
    /// folder's leftover ("(other N)"), so this is reached only by paging a thousand times into one folder.
    /// </summary>
    private const int MaxChildrenCap = 1 << 16;
    private const int FilesCap = 500;
    private const int FinishedJobsKept = 50;

    private readonly Dictionary<char, DriveScan> _drives = [];
    private readonly Lock _drivesLock = new();
    private readonly Gate _gate = new();
    private readonly DeletePool _deletePool = new("delete", DeletePool.DefaultThreadCount());

    /// <summary>
    /// The job list. Its lock is also held while a finished job is reconciled (tree, drive cache, pruning, journal,
    /// active count) and while the journal is written, so a reader that sees a job as finished also sees all of its
    /// effects, and journal writes land in the same order as the job-list changes they record.
    /// </summary>
    private readonly List<Job> _jobs = [];
    private readonly Lock _jobsLock = new();
    private readonly Journal _journal;
    private readonly DriveCache _driveCache;
    private readonly FsWatcher _fs;
    private readonly Thread _journalTicker;
    private readonly ManualResetEventSlim _stopping = new();
    private int _activeJobs;
    /// <summary>Next job id; guarded by <see cref="_jobsLock"/>.</summary>
    private ulong _nextJobId = 1;

    /// <summary>
    /// Creates the host: opens the journal in <paramref name="dataDir"/> (without resuming anything yet; see
    /// <see cref="ResumeJournaledJobs"/>), starts the drive cache, the journal ticker and the filesystem worker.
    /// </summary>
    public EngineHost(string dataDir)
    {
        _journal = new Journal(dataDir);
        _driveCache = DriveCache.Start();
        _fs = new FsWatcher(ProcessFsBatch, () => ActiveJobs == 0);
        _journalTicker = new Thread(RunJournalTicker) { Name = "journal-ticker", IsBackground = true };
        _journalTicker.Start();
    }

    /// <summary>Number of deletion jobs queued or running.</summary>
    public int ActiveJobs => Volatile.Read(ref _activeJobs);

    /// <summary>Mounted volumes from the background-refreshed cache (never blocks on a slow drive).</summary>
    public IReadOnlyList<DriveInfo> ListDrives() => _driveCache.Get();

    /// <summary>
    /// Starts scanning a drive (idempotent: an already opened drive just returns its root id) and watching it for
    /// external changes. Returns the root node id, e.g. <c>"C:0"</c>.
    /// </summary>
    /// <param name="drive">A drive letter, optionally followed by a colon.</param>
    public string StartScan(string drive)
    {
        var letter = ParseLetter(drive);
        return StartScanCore(letter, () => new DriveScan(letter), watch: true);
    }

    /// <summary>Progress of a drive's scan; <see cref="ScanState.Idle"/> for a drive that was never opened.</summary>
    public ScanStatus ScanStatus(string drive)
    {
        var letter = ParseLetter(drive);
        return Drive(letter)?.StatusView() ?? new ScanStatus(letter.ToString(), ScanState.Idle, 0, 0, 0, 0);
    }

    /// <summary>Batched node views for every expanded row, all with the same child window. Unknown or deleted ids are omitted.</summary>
    /// <param name="ids">Node ids.</param>
    /// <param name="maxChildren">Subfolders listed per node, clamped to 1..65,536; the rest are folded.</param>
    public IReadOnlyList<NodeView> GetNodes(IReadOnlyList<string> ids, int maxChildren) =>
        GetNodes(ids.Select(id => new NodeRequest(id, maxChildren)).ToList());

    /// <summary>
    /// Batched node views for every expanded row, each with its own child window: a folder whose leftover pages
    /// ("(other N)") are open needs more of its subfolders listed than the others. Unknown or deleted ids are omitted.
    /// </summary>
    /// <param name="requests">Node ids, each with the subfolders to list for it, clamped to 1..65,536; the rest are folded.</param>
    public IReadOnlyList<NodeView> GetNodes(IReadOnlyList<NodeRequest> requests)
    {
        var views = new List<NodeView>(requests.Count);
        foreach (var r in requests)
        {
            var maxChildren = Math.Clamp(r.MaxChildren, 1, MaxChildrenCap);
            if (TryResolve(r.Id, out var drive, out var idx, out _) && drive.NodeView(idx, maxChildren) is { } view)
            {
                views.Add(view);
            }
        }
        return views;
    }

    /// <summary>Re-reads a folder and returns its loose files, largest first (at most 500). Blocks on the listing.</summary>
    public IReadOnlyList<FileView> ListFiles(string id)
    {
        var (drive, idx) = Resolve(id);
        try
        {
            return drive.ListFiles(idx, FilesCap);
        }
        catch (Win32Exception e)
        {
            throw new EngineException(e.Message, e);
        }
    }

    /// <summary>
    /// Starts deleting a folder or one loose file and returns the job id. Only one deletion runs at a time; scanning
    /// pauses while it runs and the tree is reconciled when it ends.
    /// </summary>
    public string StartDelete(DeleteTarget target, DeleteMode mode)
    {
        var (drive, idx) = Resolve(target.NodeId);
        var node = drive.Node(idx) ?? throw new EngineException("unknown node");
        if (node.State == NodeState.Deleted)
        {
            throw new EngineException("that folder has already been deleted");
        }

        Job job;
        JobContext ctx;
        lock (_jobsLock)
        {
            // Checked under the job-list lock so two concurrent requests cannot both pass.
            if (ActiveJobs > 0)
            {
                throw new EngineException("another deletion is still running");
            }

            WidePath path;
            bool isDir;
            ulong? expectedBytes;
            ulong? expectedFiles;
            NodeRef? nodeRef;
            ulong? fileSize;
            if (target.FileName is { } name)
            {
                ValidateFileName(name);
                path = drive.PathOf(idx).Join(name);
                var attrs = WinFs.AttributesOf(path) ?? throw new EngineException($"{path.Display} not found");
                if (WinFs.IsDir(attrs))
                {
                    throw new EngineException($"{path.Display} is a folder");
                }
                var size = FileSizeOf(path);
                fileSize = size;
                (isDir, expectedBytes, expectedFiles, nodeRef) = (false, size, 1, null);
            }
            else
            {
                if (node.Parent == DriveScan.NoParent)
                {
                    throw new EngineException("refusing to delete a drive root");
                }
                fileSize = null;
                path = drive.PathOf(idx);
                (isDir, expectedBytes, expectedFiles, nodeRef) = (true, node.TotalBytes, node.TotalFiles, new NodeRef(drive.Letter, idx));
            }
            var expectedComplete = drive.Status == ScanState.Done;

            var id = _nextJobId++;
            job = new Job(id, path, mode, isDir, nodeRef, expectedBytes, expectedFiles, expectedComplete);
            if (isDir)
            {
                node.DeletingJob = id;
            }
            _jobs.Add(job);
            Interlocked.Increment(ref _activeJobs);
            _fs.Suppress = true; // don't react to the deletion's own filesystem events
            SaveJournalLocked();
            ctx = new JobContext(drive, idx, fileSize);
        }
        SpawnJob(job, ctx);
        return job.Id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Asks a running permanent deletion to stop. A Recycle Bin operation cannot be interrupted.</summary>
    public void CancelDelete(string jobId)
    {
        var job = FindJob(jobId);
        if (!job.Status.IsActive())
        {
            throw new EngineException("that deletion has already finished");
        }
        if (job.Mode == DeleteMode.Recycle)
        {
            throw new EngineException("a Recycle Bin operation cannot be interrupted once started");
        }
        job.RequestCancel();
    }

    /// <summary>Every known job (active ones and the most recent finished ones).</summary>
    public IReadOnlyList<JobView> GetJobs()
    {
        lock (_jobsLock)
        {
            return _jobs.ConvertAll(j => j.ToView());
        }
    }

    /// <summary>Removes a finished job from the list and the journal.</summary>
    public void DismissJob(string jobId)
    {
        var job = FindJob(jobId);
        if (job.Status.IsActive())
        {
            throw new EngineException("that deletion is still running");
        }
        lock (_jobsLock)
        {
            _jobs.RemoveAll(j => j.Id == job.Id);
            SaveJournalLocked();
        }
    }

    /// <summary>Re-lists a folder's subtree from scratch on a background thread.</summary>
    public void Rescan(string id)
    {
        var (drive, idx) = Resolve(id);
        if (ActiveJobs > 0)
        {
            throw new EngineException("cannot rescan while a deletion is running");
        }
        new Thread(() =>
        {
            drive.BeginBfs();
            _gate.PauseAndDrain();
            drive.ResetSubtree(idx);
            _gate.Resume();
            drive.CompleteBfs(_gate, idx);
        })
        {
            Name = $"rescan-{id}",
            IsBackground = true,
        }.Start();
    }

    /// <summary>
    /// One poll tick: drives, jobs, the requested nodes and the scan status of the requested letters. Malformed
    /// letters are skipped rather than failing the whole poll.
    /// </summary>
    /// <param name="requestedIds">Node ids of the expanded rows.</param>
    /// <param name="letters">Drive letters whose scan status the UI shows.</param>
    /// <param name="maxChildren">Subfolders listed per node, clamped to 1..65,536.</param>
    public PollResult Poll(IReadOnlyList<string> requestedIds, IReadOnlyList<string> letters, int maxChildren = DefaultMaxChildren) =>
        Poll(requestedIds.Select(id => new NodeRequest(id, maxChildren)).ToList(), letters);

    /// <summary>
    /// One poll tick: drives, jobs, the requested nodes (each with its own child window) and the scan status of the
    /// requested letters. Malformed letters are skipped rather than failing the whole poll.
    /// </summary>
    /// <param name="requests">The expanded rows' nodes, each with the subfolders to list for it.</param>
    /// <param name="letters">Drive letters whose scan status the UI shows.</param>
    public PollResult Poll(IReadOnlyList<NodeRequest> requests, IReadOnlyList<string> letters)
    {
        var scans = new List<ScanStatus>(letters.Count);
        foreach (var l in letters)
        {
            if (TryParseLetter(l, out _))
            {
                scans.Add(ScanStatus(l));
            }
        }
        var nodes = requests.Count > 0 ? GetNodes(requests) : [];
        return new PollResult(ListDrives(), GetJobs(), nodes, scans);
    }

    /// <summary>
    /// The window wants to close. Returns false while a deletion runs (the UI should ask the user and call
    /// <see cref="ForceClose"/> to close anyway); otherwise saves the journal and returns true.
    /// </summary>
    public bool RequestClose()
    {
        if (ActiveJobs > 0)
        {
            return false;
        }
        SaveJournal();
        return true;
    }

    /// <summary>
    /// Saves the journal so running deletions resume on the next launch; the UI exits right after. Running jobs are
    /// not cancelled (cancelling would record them as finished and they would not resume).
    /// </summary>
    public void ForceClose() => SaveJournal();

    /// <summary>Loads the journal and restarts every job that was still active when the app last ran.</summary>
    public void ResumeJournaledJobs()
    {
        var records = _journal.Load();
        var toResume = new List<Job>();
        lock (_jobsLock)
        {
            var maxId = records.Count == 0 ? 0 : records.Max(r => r.Id);
            _nextJobId = Math.Max(_nextJobId, maxId + 1);
            foreach (var rec in records)
            {
                var job = Job.FromRecord(rec);
                if (job.Status.IsActive())
                {
                    toResume.Add(job);
                }
                _jobs.Add(job);
            }
            if (toResume.Count > 0)
            {
                Interlocked.Add(ref _activeJobs, toResume.Count);
                _fs.Suppress = true;
            }
        }
        foreach (var job in toResume)
        {
            Trace.TraceInformation($"resuming deletion {job.Id} of {job.Path}");
            SpawnJob(job, null);
        }
    }

    /// <summary>
    /// Stops the background threads (journal ticker, watchers, drive cache, scans). A deletion that is still running is
    /// left alone: its threads are background threads and the journal resumes it on the next launch.
    /// </summary>
    public void Dispose()
    {
        if (_stopping.IsSet)
        {
            return;
        }
        _stopping.Set();
        _journalTicker.Join();
        _fs.Dispose();
        _driveCache.Dispose();
        lock (_drivesLock)
        {
            foreach (var scan in _drives.Values)
            {
                scan.Cancel();
            }
        }
        if (ActiveJobs == 0)
        {
            _deletePool.Dispose();
        }
    }

    /// <summary>
    /// Like <see cref="StartScan"/>, but rooted at an arbitrary directory and without a filesystem watcher. For tests,
    /// which have no spare drive letter to scan.
    /// </summary>
    internal string StartScanAt(char letter, string rootDisplayPath)
    {
        letter = char.ToUpperInvariant(letter);
        return StartScanCore(letter, () => DriveScan.WithRoot(letter, rootDisplayPath), watch: false);
    }

    /// <summary>
    /// Splits an absolute Windows path into its uppercase drive letter and the components below the root, e.g.
    /// <c>C:\Users\steve</c> → <c>('C', ["Users", "steve"])</c>. <see langword="null"/> if it isn't a drive-letter path.
    /// </summary>
    internal static (char Letter, string[] Components)? SplitDrivePath(string path)
    {
        var s = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        if (s.Length < 2 || !char.IsAsciiLetter(s[0]) || s[1] != ':')
        {
            return null;
        }
        return (char.ToUpperInvariant(s[0]), s[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static char ParseLetter(string drive) =>
        TryParseLetter(drive, out var letter) ? letter : throw new EngineException($"bad drive {RustDebug(drive)}");

    /// <summary>Accepts <c>"C"</c> or <c>"C:..."</c> (only the first two characters are looked at, as in the Rust build).</summary>
    private static bool TryParseLetter(string drive, out char letter)
    {
        letter = default;
        if (drive.Length == 0 || !char.IsAsciiLetter(drive[0]) || (drive.Length > 1 && drive[1] != ':'))
        {
            return false;
        }
        letter = char.ToUpperInvariant(drive[0]);
        return true;
    }

    private static void ValidateFileName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.AsSpan().IndexOfAny('\\', '/', '\0') >= 0)
        {
            throw new EngineException($"invalid file name {RustDebug(name)}");
        }
    }

    /// <summary>Size of a file without following reparse points (0 if it cannot be read).</summary>
    private static ulong FileSizeOf(WidePath path)
    {
        try
        {
            return (ulong)new FileInfo(path.Wide).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Quotes a string the way Rust's <c>{:?}</c> does, so error messages match the Rust build exactly.</summary>
    private static string RustDebug(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            _ = c switch
            {
                '"' => sb.Append("\\\""),
                '\\' => sb.Append(@"\\"),
                '\n' => sb.Append(@"\n"),
                '\r' => sb.Append(@"\r"),
                '\t' => sb.Append(@"\t"),
                '\0' => sb.Append(@"\0"),
                _ when char.IsControl(c) => sb.Append(CultureInfo.InvariantCulture, $"\\u{{{(int)c:x}}}"),
                _ => sb.Append(c),
            };
        }
        return sb.Append('"').ToString();
    }

    private string StartScanCore(char letter, Func<DriveScan> create, bool watch)
    {
        DriveScan scan;
        lock (_drivesLock)
        {
            if (_drives.TryGetValue(letter, out var existing))
            {
                return existing.NodeId(0);
            }
            scan = create();
            _drives.Add(letter, scan);
        }
        if (watch)
        {
            _fs.Watch(letter);
        }
        new Thread(() => scan.RunBfs(_gate, 0)) { Name = $"scan-{letter}", IsBackground = true }.Start();
        return scan.NodeId(0);
    }

    private DriveScan? Drive(char letter)
    {
        lock (_drivesLock)
        {
            return _drives.GetValueOrDefault(letter);
        }
    }

    private (DriveScan Drive, uint Index) Resolve(string id) =>
        TryResolve(id, out var drive, out var idx, out var error) ? (drive, idx) : throw new EngineException(error);

    private bool TryResolve(string id, out DriveScan drive, out uint index, out string error)
    {
        drive = null!;
        index = 0;
        if (DriveScan.ParseNodeId(id) is not { } nodeRef)
        {
            error = $"bad node id {RustDebug(id)}";
            return false;
        }
        if (Drive(nodeRef.Letter) is not { } scan)
        {
            error = $"drive {nodeRef.Letter}: is not scanned";
            return false;
        }
        if (scan.Node(nodeRef.Index) is null)
        {
            error = $"unknown node {id}";
            return false;
        }
        (drive, index, error) = (scan, nodeRef.Index, "");
        return true;
    }

    private Job FindJob(string id)
    {
        if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            throw new EngineException($"bad job id {RustDebug(id)}");
        }
        lock (_jobsLock)
        {
            return _jobs.Find(j => j.Id == n) ?? throw new EngineException($"unknown job {n}");
        }
    }

    /// <summary>Runs a job on its own thread with scanning paused, then reconciles the scan tree and the journal.</summary>
    private void SpawnJob(Job job, JobContext? ctx)
    {
        var thread = new Thread(() => RunJob(job, ctx)) { Name = $"job-{job.Id}", IsBackground = true };
        if (job.Mode == DeleteMode.Recycle)
        {
            // The shell's copy engine wants a single-threaded apartment; this way it runs right on the job thread.
            thread.SetApartmentState(ApartmentState.STA);
        }
        thread.Start();
    }

    private void RunJob(Job job, JobContext? ctx)
    {
        _gate.PauseAndDrain();
        var outcome = Deleter.Execute(job, _deletePool);

        DriveScan? rescan = null;
        uint rescanIndex = 0;
        lock (_jobsLock)
        {
            // Publish the outcome and every effect of it in one step under the job-list lock (see _jobs).
            job.Finish(outcome);
            if (ctx is not null)
            {
                switch (outcome, ctx.FileSize)
                {
                    case (JobStatus.Done, { } size):
                        ctx.Drive.SubtractFile(ctx.NodeIndex, size);
                        break;
                    case (JobStatus.Done, null):
                        ctx.Drive.RemoveNode(ctx.NodeIndex);
                        break;
                    case (_, { }):
                        break;
                    default:
                        // Partially deleted (cancelled / errors / failed): re-list what is left.
                        ctx.Drive.ResetSubtree(ctx.NodeIndex);
                        (rescan, rescanIndex) = (ctx.Drive, ctx.NodeIndex);
                        break;
                }
                if (ctx.Drive.Node(ctx.NodeIndex) is { } node)
                {
                    node.DeletingJob = 0;
                }
            }

            _driveCache.Invalidate();
            PruneJobsLocked();
            SaveJournalLocked();
            // Announce the rescan before the job stops counting as active, so "no active jobs and scan done" really
            // means the tree is settled.
            rescan?.BeginBfs();
            if (Interlocked.Decrement(ref _activeJobs) == 0)
            {
                _gate.Resume();
                _fs.Suppress = false; // resume watching external changes
            }
        }
        rescan?.CompleteBfs(_gate, rescanIndex);
    }

    /// <summary>Keeps the job list bounded: every active job plus the most recent finished ones.</summary>
    private void PruneJobsLocked()
    {
        var finished = _jobs.Count(j => !j.Status.IsActive());
        var toDrop = finished - FinishedJobsKept;
        if (toDrop <= 0)
        {
            return;
        }
        _jobs.RemoveAll(j =>
        {
            if (toDrop > 0 && !j.Status.IsActive())
            {
                toDrop--;
                return true;
            }
            return false;
        });
    }

    private void SaveJournal()
    {
        lock (_jobsLock)
        {
            SaveJournalLocked();
        }
    }

    private void SaveJournalLocked()
    {
        try
        {
            _journal.Save(_jobs.ConvertAll(j => j.ToRecord()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"journal: save failed: {e.Message}");
        }
    }

    /// <summary>Saves the journal once a second while jobs are active, so progress survives a crash.</summary>
    private void RunJournalTicker()
    {
        while (!_stopping.Wait(TimeSpan.FromSeconds(1)))
        {
            if (ActiveJobs > 0)
            {
                SaveJournal();
            }
        }
    }

    /// <summary>Refreshes the tree node for each changed directory in a debounced batch.</summary>
    private void ProcessFsBatch(HashSet<string> dirs)
    {
        var touched = false;
        foreach (var dir in dirs)
        {
            if (SplitDrivePath(dir) is not { } split)
            {
                continue;
            }
            var (letter, components) = split;
            if (Drive(letter) is not { } scan)
            {
                continue; // drive not opened: nothing to update
            }
            if (scan.Status != ScanState.Done)
            {
                continue; // the in-progress initial scan already reflects current state
            }
            scan.RefreshDir(_gate, scan.FindNode(components));
            touched = true;
        }
        if (touched)
        {
            _driveCache.Invalidate(); // free space may have changed
        }
    }

    /// <summary>Node context for a job started from the UI: the drive, the node and (for a file) its size.</summary>
    private sealed record JobContext(DriveScan Drive, uint NodeIndex, ulong? FileSize);
}
