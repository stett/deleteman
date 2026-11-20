using System.Diagnostics;
using System.Text;
using DeleteMan.App.Dialogs;
using DeleteMan.Engine;
using DeleteMan.ViewModels;
using Microsoft.UI.Xaml;

namespace DeleteMan.App.Shell;

/// <summary>
/// The app's behaviour (port of main.ts): the 250 ms poll loop against the engine, and every user action.
/// Everything here runs on the UI thread; engine calls are pushed to the thread pool with <see cref="Task.Run(Action)"/>
/// and their results applied back on the UI thread by the awaits.
/// </summary>
internal sealed class AppController
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly EngineHost _engine;
    private readonly Model _model = new();
    private readonly RowsPresenter _rows;
    private readonly JobsPresenter _jobs;
    private readonly Func<XamlRoot?> _xamlRoot;

    /// <summary>Jobs seen running or queued this session; only those pop an error dialog when they finish badly.</summary>
    private readonly HashSet<string> _seenActive = [];
    private readonly HashSet<string> _reported = [];

    private bool _inFlight;
    private bool _again;
    private bool _stopped;
    private TaskCompletionSource? _wake;

    /// <summary>Wires the presenters' events to the actions.</summary>
    public AppController(EngineHost engine, RowsPresenter rows, JobsPresenter jobs, Func<XamlRoot?> xamlRoot)
    {
        _engine = engine;
        _rows = rows;
        _jobs = jobs;
        _xamlRoot = xamlRoot;
        Perf.Start();

        _rows.SegmentInvoked += (row, seg) => _ = OnSegmentAsync(row.Letter, row.Index, seg.Key);
        _rows.CloseInvoked += row => OnClose(row.Letter, row.Index);
        _rows.PinInvoked += row => _ = OnPinAsync(row.Letter, row.Index);
        _rows.DeleteInvoked += (row, mode) => _ = OnDeleteAsync(row.Letter, row.Index, mode);
        _rows.CancelInvoked += jobId => _ = OnCancelAsync(jobId);
        _jobs.CancelInvoked += jobId => _ = OnCancelAsync(jobId);
        _jobs.DismissInvoked += jobId => _ = OnDismissAsync(jobId);
        _jobs.DetailsInvoked += jobId => _ = ShowDetailsAsync(jobId);
    }

    /// <summary>Resumes journaled jobs once, then polls until <see cref="Stop"/>.</summary>
    public async Task RunAsync()
    {
        // Exactly once, before the first poll, so resumed jobs are in the first job list. A failure here must not
        // stop the UI from polling: the journal is set aside by the engine when corrupt, anything else is logged.
        try
        {
            await Task.Run(_engine.ResumeJournaledJobs);
        }
        catch (Exception e)
        {
            Trace.TraceError($"resume failed: {e}");
        }
        while (!_stopped)
        {
            _again = false;
            _inFlight = true;
            try
            {
                await PollOnceAsync();
            }
            catch (Exception e)
            {
                Trace.TraceError($"poll failed: {e}");
            }
            _inFlight = false;
            if (_again || _stopped)
            {
                continue;
            }
            _wake = new TaskCompletionSource();
            await Task.WhenAny(Task.Delay(PollInterval), _wake.Task);
            _wake = null;
        }
    }

    /// <summary>Ends the poll loop (window closing).</summary>
    public void Stop()
    {
        _stopped = true;
        _wake?.TrySetResult();
    }

    /// <summary>Polls immediately, or right after the poll in flight. Never two polls at once.</summary>
    public void PollNow()
    {
        if (_inFlight)
        {
            _again = true;
            return;
        }
        _wake?.TrySetResult();
    }

    // ---------- Polling ----------

    private async Task PollOnceAsync()
    {
        IReadOnlyList<NodeRequest> requests = _model.NodeRequests();
        IReadOnlyList<string> requested = requests.Select(q => q.Id).ToList();
        IReadOnlyList<string> letters = _model.LettersWithUsedRow();
        PollResult r;
        using (Perf.Phase("poll.engine (worker)"))
        {
            r = await Task.Run(() => _engine.Poll(requests, letters));
        }
        using (Perf.Phase("poll.apply"))
        {
            _model.ApplyPoll(r.Drives, r.Jobs, r.Nodes, requested, r.Scans);
        }
        RenderWithPins();
        if (await RefreshFileListsAsync())
        {
            // File rows follow their folders' new lists: a deleted file's row closes, and a files row whose folder
            // amounts to one file becomes that file's row.
            _model.ReconcileFileRows();
            Render();
        }
        ReportFinishedJobs();
    }

    /// <summary>
    /// Lets the pins open the largest folders (with whatever node data is already here; deeper levels follow as
    /// polls bring their nodes), then renders. Pins are held off while a deletion runs, so the rows stay put under
    /// the dimmed UI. The rows the pin pass ends on are the ones rendered; nothing is built twice.
    /// </summary>
    private void RenderWithPins()
    {
        IReadOnlyList<RowView> rows;
        if (Jobs.IsBusy(_model.Jobs))
        {
            using (Perf.Phase("render.build"))
            {
                rows = Rows.Build(_model);
            }
        }
        else
        {
            using (Perf.Phase("pins.apply"))
            {
                rows = Pins.ApplyAndBuild(_model, out _);
            }
        }
        Render(rows);
    }

    private void Render()
    {
        IReadOnlyList<RowView> rows;
        using (Perf.Phase("render.build"))
        {
            rows = Rows.Build(_model);
        }
        Render(rows);
    }

    private void Render(IReadOnlyList<RowView> rows)
    {
        using (Perf.Phase("render.rows"))
        {
            _rows.Render(rows, Jobs.IsBusy(_model.Jobs));
        }
        using (Perf.Phase("render.jobs"))
        {
            _jobs.Render(Jobs.BuildLines(_model.Jobs));
        }
    }

    /// <summary>Fetches the loose-file lists of open files rows whose folder changed. True if any was fetched.</summary>
    private async Task<bool> RefreshFileListsAsync()
    {
        IReadOnlyList<string> stale = _model.StaleFileLists();
        if (stale.Count == 0)
        {
            return false;
        }
        IReadOnlyList<FileView>[] lists = await Task.WhenAll(stale.Select(id => Task.Run(() =>
        {
            try
            {
                return _engine.ListFiles(id);
            }
            catch (EngineException e)
            {
                Trace.TraceError($"list_files failed: {e.Message}");
                return Array.Empty<FileView>();
            }
        })));
        for (int i = 0; i < stale.Count; i++)
        {
            _model.Files[stale[i]] = lists[i];
        }
        return true;
    }

    private void ReportFinishedJobs()
    {
        foreach (JobView j in _model.Jobs)
        {
            if (Jobs.IsActive(j))
            {
                _seenActive.Add(j.Id);
                continue;
            }
            if (!_seenActive.Contains(j.Id) || !_reported.Add(j.Id))
            {
                continue;
            }
            if (j.Status is JobStatus.DoneWithErrors or JobStatus.Failed)
            {
                _ = ShowDetailsAsync(j.Id);
            }
        }
    }

    // ---------- Actions ----------

    private IReadOnlyList<ChainEntry> ChainOf(string letter) =>
        _model.Chains.TryGetValue(letter, out IReadOnlyList<ChainEntry>? chain) ? chain : Array.Empty<ChainEntry>();

    private void SetChain(string letter, IReadOnlyList<ChainEntry> chain)
    {
        _model.Chains[letter] = chain;
        RenderWithPins();
        PollNow();
    }

    /// <summary>The pin toggle of row <paramref name="index"/>; on a collapsed drive row it starts the scan and pins the root.</summary>
    private async Task OnPinAsync(string letter, int index)
    {
        if (Jobs.IsBusy(_model.Jobs))
        {
            return;
        }
        IReadOnlyList<ChainEntry> current = ChainOf(letter);
        if (current.Count > 0)
        {
            SetChain(letter, Pins.Toggle(current, index));
            return;
        }
        if (index >= 0)
        {
            return;
        }
        try
        {
            string root = await Task.Run(() => _engine.StartScan(letter));
            if (ChainOf(letter).Count == 0)
            {
                // The drive's own pin opens the root under it (the root row's pin stays the user's to set).
                SetChain(letter, (ChainEntry[])[Chain.UsedEntry(root) with { DrivePin = true, Auto = true }]);
            }
        }
        catch (Exception e)
        {
            await ShowMessageAsync("Could not start the scan", e.Message);
        }
    }

    private async Task OnSegmentAsync(string letter, int index, string segKey)
    {
        if (Jobs.IsBusy(_model.Jobs))
        {
            return;
        }
        if (index < 0)
        {
            if (ChainOf(letter).Count > 0)
            {
                SetChain(letter, Array.Empty<ChainEntry>());
                return;
            }
            try
            {
                string root = await Task.Run(() => _engine.StartScan(letter));
                if (ChainOf(letter).Count == 0)
                {
                    SetChain(letter, (ChainEntry[])[Chain.UsedEntry(root)]);
                }
            }
            catch (Exception e)
            {
                // EngineException carries a user-facing message; anything else is a bug, but still better shown than lost.
                await ShowMessageAsync("Could not start the scan", e.Message);
            }
            return;
        }
        IReadOnlyList<ChainEntry> current = ChainOf(letter);
        if (index >= current.Count)
        {
            return;
        }
        // A click is the user's own row: it pauses any pin above it, and collapsing a pin-opened row releases them.
        SetChain(letter, Pins.Expand(current, index, Chain.EntryFor(current[index], segKey)));
    }

    private void OnClose(string letter, int index)
    {
        if (Jobs.IsBusy(_model.Jobs))
        {
            return;
        }
        SetChain(letter, Pins.Close(ChainOf(letter), index));
    }

    /// <summary>Recycle Bin / permanent delete of row <paramref name="index"/>: its folder, or for a file row the one file it shows.</summary>
    private async Task OnDeleteAsync(string letter, int index, DeleteMode mode)
    {
        if (Jobs.IsBusy(_model.Jobs))
        {
            return;
        }
        IReadOnlyList<ChainEntry> chain = ChainOf(letter);
        if (index < 0 || index >= chain.Count || !_model.Nodes.TryGetValue(chain[index].NodeId, out NodeView? node))
        {
            return;
        }
        string? fileName = chain[index].Kind == EntryKind.File ? Chain.FileName(chain[index]) : null;

        DeleteConfirmInfo info;
        DeleteTarget target;
        if (fileName is not null)
        {
            FileView? f = _model.Files.TryGetValue(node.Id, out IReadOnlyList<FileView>? files)
                ? files.FirstOrDefault(x => x.Name == fileName)
                : null;
            if (f is null)
            {
                return;
            }
            string path = Paths.JoinPath(node.Path, f.Name);
            info = new DeleteConfirmInfo(f.Name, path, Format.Bytes(f.Size), mode == DeleteMode.Permanent, ScanIncomplete: false, Paths.IsSystemRoot(path));
            target = new DeleteTarget(node.Id, f.Name);
        }
        else
        {
            if (node.IsRoot)
            {
                return;
            }
            bool partial = !_model.Scans.TryGetValue(letter, out ScanStatus? scan) || scan.Status != ScanState.Done;
            string counts = $"{Format.Bytes(node.TotalBytes)} · {Format.Plural(node.TotalFiles, "file")} · {Format.Plural(node.TotalDirs, "folder")}";
            info = new DeleteConfirmInfo(node.Name, node.Path, counts, mode == DeleteMode.Permanent, partial, Paths.IsSystemRoot(node.Path));
            target = new DeleteTarget(node.Id);
        }

        if (_xamlRoot() is not { } root || !await Dialogs.Dialogs.ConfirmDeleteAsync(root, info))
        {
            return;
        }
        try
        {
            await Task.Run(() => _engine.StartDelete(target, mode));
        }
        catch (Exception e)
        {
            await ShowMessageAsync("Could not start the deletion", e.Message);
        }
        PollNow();
    }

    private async Task OnCancelAsync(string jobId)
    {
        JobView? job = _model.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null || !Jobs.IsActive(job) || _xamlRoot() is not { } root)
        {
            return;
        }
        string already = $"Already deleted {Format.Plural(job.FilesDeleted, "file")} ({Format.Bytes(job.BytesDeleted)}).";
        if (!await Dialogs.Dialogs.ConfirmCancelAsync(root, job.Path, already))
        {
            return;
        }
        try
        {
            await Task.Run(() => _engine.CancelDelete(jobId));
        }
        catch (Exception e)
        {
            await ShowMessageAsync("Could not stop the deletion", e.Message);
        }
        PollNow();
    }

    private async Task OnDismissAsync(string jobId)
    {
        try
        {
            await Task.Run(() => _engine.DismissJob(jobId));
        }
        catch (EngineException e)
        {
            Trace.TraceError($"dismiss_job failed: {e.Message}");
        }
        PollNow();
    }

    private async Task ShowDetailsAsync(string jobId)
    {
        JobView? job = _model.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null || _xamlRoot() is not { } root)
        {
            return;
        }
        // render.ts showJobErrors: first 200 errors, "path — message (code)" per line.
        IReadOnlyList<JobError> shown = job.Errors.Take(200).ToList();
        string more = job.ErrorCount > (ulong)shown.Count ? $" (showing the first {shown.Count})" : "";
        var list = new StringBuilder();
        foreach (JobError e in shown)
        {
            if (list.Length > 0)
            {
                list.Append('\n');
            }
            list.Append(e.Path).Append(" — ").Append(e.Message).Append(" (").Append(e.Code).Append(')');
        }
        var info = new JobErrorsInfo(
            job.Status == JobStatus.Failed ? "Deletion failed" : "Deletion finished with errors",
            job.Path,
            string.IsNullOrEmpty(job.Message) ? null : job.Message,
            job.ErrorCount > 0 ? $"{Format.Plural(job.ErrorCount, "error")}{more}:" : null,
            list.ToString());
        if (await Dialogs.Dialogs.ShowJobErrorsAsync(root, info))
        {
            await OnDismissAsync(jobId);
        }
    }

    private async Task ShowMessageAsync(string title, string text)
    {
        if (_xamlRoot() is { } root)
        {
            await Dialogs.Dialogs.ShowMessageAsync(root, title, text);
        }
    }
}
