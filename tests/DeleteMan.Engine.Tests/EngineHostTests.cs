using DeleteMan.Engine.Tests.TestSupport;

namespace DeleteMan.Engine.Tests;

/// <summary>
/// The host that owns scans, jobs, the journal and the watcher, and exposes the commands the UI calls. Error
/// strings are part of the contract: the UI shows them verbatim.
/// </summary>
public class EngineHostTests
{
    private static EngineHost NewHost(TempDir tmp) => new(tmp.Sub("data"));

    private static bool ScanDone(EngineHost host, string letter) => host.ScanStatus(letter).Status == ScanState.Done;

    [Fact]
    public void Validation_errors_use_the_documented_messages()
    {
        using var tmp = new TempDir();
        using var host = NewHost(tmp);

        var bad = Assert.Throws<EngineException>(() => host.StartScan("??"));
        Assert.StartsWith("bad drive", bad.Message);
        Assert.StartsWith("bad drive", Assert.Throws<EngineException>(() => host.ScanStatus("")).Message);

        var idle = host.ScanStatus("Q");
        Assert.Equal("Q", idle.Drive);
        Assert.Equal(ScanState.Idle, idle.Status);
        Assert.Equal(0UL, idle.DirsScanned);

        Assert.Empty(host.GetNodes(["Q:0", "garbage"], 64));

        Assert.Equal("bad node id \"garbage\"", Assert.Throws<EngineException>(() => host.ListFiles("garbage")).Message);
        Assert.Equal("drive Q: is not scanned", Assert.Throws<EngineException>(() => host.ListFiles("Q:0")).Message);
        Assert.Equal("drive Q: is not scanned", Assert.Throws<EngineException>(() => host.StartDelete(new DeleteTarget("Q:0"), DeleteMode.Permanent)).Message);
        Assert.Equal("bad job id \"abc\"", Assert.Throws<EngineException>(() => host.CancelDelete("abc")).Message);
        Assert.Equal("unknown job 999", Assert.Throws<EngineException>(() => host.CancelDelete("999")).Message);
        Assert.Equal("unknown job 999", Assert.Throws<EngineException>(() => host.DismissJob("999")).Message);
        Assert.Equal("drive Q: is not scanned", Assert.Throws<EngineException>(() => host.Rescan("Q:0")).Message);
        Assert.Empty(host.GetJobs());
        Assert.Equal(0, host.ActiveJobs);
    }

    [Fact]
    public void Scan_delete_and_journal_roundtrip()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("vol");
        Fs.Write(Path.Combine(root, "a", "x.bin"), 100);
        Fs.Write(Path.Combine(root, "a", "deep", "y.bin"), 50);
        Fs.Write(Path.Combine(root, "b", "w.bin"), 10);
        Fs.Write(Path.Combine(root, "loose.bin"), 5);

        using var host = NewHost(tmp);
        var rootId = host.StartScanAt('T', root);
        Assert.Equal("T:0", rootId);
        Assert.Equal(rootId, host.StartScanAt('T', root)); // idempotent
        Assert.True(Fs.WaitUntil(() => ScanDone(host, "T")));

        var rootView = Assert.Single(host.GetNodes([rootId], 64));
        Assert.Equal(165UL, rootView.TotalBytes);
        Assert.Equal(["a", "b"], rootView.Children.Select(c => c.Name));
        var a = rootView.Children[0];

        // Drive roots are protected, unknown file names rejected, and a folder is not a file.
        Assert.Equal("refusing to delete a drive root", Assert.Throws<EngineException>(() => host.StartDelete(new DeleteTarget(rootId), DeleteMode.Permanent)).Message);
        Assert.StartsWith("invalid file name", Assert.Throws<EngineException>(() => host.StartDelete(new DeleteTarget(rootId, "..\\x"), DeleteMode.Permanent)).Message);
        Assert.EndsWith("not found", Assert.Throws<EngineException>(() => host.StartDelete(new DeleteTarget(rootId, "nope.bin"), DeleteMode.Permanent)).Message);
        Assert.EndsWith("is a folder", Assert.Throws<EngineException>(() => host.StartDelete(new DeleteTarget(rootId, "a"), DeleteMode.Permanent)).Message);

        var files = host.ListFiles(rootId);
        Assert.Equal("loose.bin", Assert.Single(files).Name);

        // Delete the loose file first (single-file job), then folder "a".
        var fileJob = host.StartDelete(new DeleteTarget(rootId, "loose.bin"), DeleteMode.Permanent);
        Assert.True(Fs.WaitUntil(() => host.GetJobs().All(j => !j.Status.IsActive())));
        Assert.False(File.Exists(Path.Combine(root, "loose.bin")));
        Assert.Equal(160UL, host.GetNodes([rootId], 64)[0].TotalBytes);
        Assert.Equal(0UL, host.GetNodes([rootId], 64)[0].OwnFiles);

        var jobId = host.StartDelete(new DeleteTarget(a.Id), DeleteMode.Permanent);
        Assert.NotEqual(fileJob, jobId);
        Assert.True(Fs.WaitUntil(() => host.GetJobs().All(j => !j.Status.IsActive())));
        Assert.False(Directory.Exists(Path.Combine(root, "a")));
        Assert.True(Directory.Exists(Path.Combine(root, "b")));

        var job = host.GetJobs().Single(j => j.Id == jobId);
        Assert.Equal(JobStatus.Done, job.Status);
        Assert.Equal(Path.Combine(root, "a"), job.Path);
        Assert.Equal(a.Id, job.NodeId);
        Assert.Equal(150UL, job.ExpectedBytes);
        Assert.Equal(2UL, job.ExpectedFiles);
        Assert.True(job.ExpectedComplete);
        Assert.Equal(2UL, job.FilesDeleted);
        Assert.Equal(150UL, job.BytesDeleted);
        Assert.Equal(2UL, job.DirsDeleted);
        Assert.Empty(job.Errors);
        Assert.Equal(0, host.ActiveJobs);

        // The tree reflects the deletion without a rescan, and the node is gone from the poll.
        var after = Assert.Single(host.GetNodes([rootId], 64));
        Assert.Equal(10UL, after.TotalBytes);
        Assert.Equal(["b"], after.Children.Select(c => c.Name));
        Assert.Empty(host.GetNodes([a.Id], 64));

        // Finished jobs can be dismissed; active-only operations reject them.
        Assert.Equal("that deletion has already finished", Assert.Throws<EngineException>(() => host.CancelDelete(jobId)).Message);
        host.DismissJob(jobId);
        Assert.DoesNotContain(host.GetJobs(), j => j.Id == jobId);
        Assert.Equal("unknown job " + jobId, Assert.Throws<EngineException>(() => host.DismissJob(jobId)).Message);

        // The journal on disk tracks the job list.
        var journal = new Journal(tmp.Sub("data"));
        Assert.True(File.Exists(journal.Path));
        var records = journal.Load();
        Assert.Single(records);
        Assert.Equal(ulong.Parse(fileJob), records[0].Id);

        // Closing is allowed when nothing runs.
        Assert.True(host.RequestClose());
    }

    [Fact]
    public void Poll_bundles_drives_jobs_nodes_and_scans()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("vol");
        Fs.Write(Path.Combine(root, "a", "x.bin"), 100);
        using var host = NewHost(tmp);
        var rootId = host.StartScanAt('T', root);
        Assert.True(Fs.WaitUntil(() => ScanDone(host, "T")));

        var poll = host.Poll([rootId, "Q:0"], ["T", "Q"]);
        Assert.NotEmpty(poll.Drives);
        Assert.Empty(poll.Jobs);
        var node = Assert.Single(poll.Nodes);
        Assert.Equal(rootId, node.Id);
        Assert.Equal(2, poll.Scans.Count);
        Assert.Equal(ScanState.Done, poll.Scans[0].Status);
        Assert.Equal("Q", poll.Scans[1].Drive);
        Assert.Equal(ScanState.Idle, poll.Scans[1].Status);

        Assert.Equal(poll.Drives.Select(d => d.Letter), host.ListDrives().Select(d => d.Letter));
    }

    [Fact]
    public void Max_children_is_clamped_and_tail_is_folded()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("vol");
        for (var i = 0; i < 12; i++)
        {
            Fs.Write(Path.Combine(root, $"d{i:D2}", "f.bin"), (i + 1) * 10);
        }
        using var host = NewHost(tmp);
        var rootId = host.StartScanAt('T', root);
        Assert.True(Fs.WaitUntil(() => ScanDone(host, "T")));

        var view = host.GetNodes([rootId], 0)[0]; // clamps to 1
        Assert.Single(view.Children);
        Assert.Equal("d11", view.Children[0].Name);
        Assert.Equal(11UL, view.OtherCount);
        Assert.Equal(660UL, view.OtherBytes);

        var all = host.GetNodes([rootId], 5000)[0]; // far more than there are: every folder is listed
        Assert.Equal(12, all.Children.Count);
        Assert.Equal(0UL, all.OtherCount);
    }

    [Fact]
    public void Each_requested_node_gets_its_own_child_window()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("vol");
        for (var i = 0; i < 5; i++)
        {
            Fs.Write(Path.Combine(root, $"d{i}", "f.bin"), (i + 1) * 10);
            Fs.Write(Path.Combine(root, "d0", $"s{i}", "f.bin"), 1);
        }
        using var host = NewHost(tmp);
        var rootId = host.StartScanAt('T', root);
        Assert.True(Fs.WaitUntil(() => ScanDone(host, "T")));
        var d0 = host.GetNodes([rootId], 64)[0].Children.Single(c => c.Name == "d0").Id;

        // The UI lists two of the root's folders but all of d0's (it has pages of its leftover open, say).
        var views = host.Poll([new NodeRequest(rootId, 2), new NodeRequest(d0, 64)], ["T"]).Nodes;
        Assert.Equal(2, views.Count);
        Assert.Equal(["d4", "d3"], views[0].Children.Select(c => c.Name));
        Assert.Equal(3UL, views[0].OtherCount);
        Assert.Equal(5, views[1].Children.Count);
        Assert.Equal(0UL, views[1].OtherCount);
    }

    [Fact]
    public void Second_job_is_refused_while_one_runs_and_cancel_stops_it()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("vol");
        // Large enough that the deletion is still running when the assertions below look at it.
        for (var i = 0; i < 3000; i++)
        {
            Fs.Write(Path.Combine(root, "big", $"d{i % 60}", $"f{i}.bin"), 16);
        }
        Fs.Write(Path.Combine(root, "other", "o.bin"), 1);
        // Hold one file so the job cannot finish instantly and leaves a partial tree.
        var held = Path.Combine(root, "big", "d0", "held.bin");
        Fs.Write(held, 1);
        using var stream = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read);

        using var host = NewHost(tmp);
        var rootId = host.StartScanAt('T', root);
        Assert.True(Fs.WaitUntil(() => ScanDone(host, "T")));
        var rootView = host.GetNodes([rootId], 64)[0];
        var big = rootView.Children.Single(c => c.Name == "big");
        var other = rootView.Children.Single(c => c.Name == "other");

        var jobId = host.StartDelete(new DeleteTarget(big.Id), DeleteMode.Permanent);
        var running = host.GetJobs().Single(j => j.Id == jobId);
        Assert.True(running.Status.IsActive());
        Assert.Equal(1, host.ActiveJobs);
        Assert.False(host.RequestClose());

        // Only one deletion at a time, and no rescans while it runs.
        var busy = Record.Exception(() => host.StartDelete(new DeleteTarget(other.Id), DeleteMode.Permanent));
        if (busy is not null)
        {
            Assert.Equal("another deletion is still running", busy.Message);
        }
        var rescan = Record.Exception(() => host.Rescan(rootId));
        if (rescan is not null)
        {
            Assert.Equal("cannot rescan while a deletion is running", rescan.Message);
        }

        host.CancelDelete(jobId);
        Assert.True(Fs.WaitUntil(() => !host.GetJobs().Single(j => j.Id == jobId).Status.IsActive()));
        var done = host.GetJobs().Single(j => j.Id == jobId);
        Assert.True(done.Status is JobStatus.Cancelled or JobStatus.DoneWithErrors, done.Status.ToString());
        Assert.True(File.Exists(held));

        // After a partial deletion the subtree is re-listed so what is left is visible.
        Assert.True(Fs.WaitUntil(() => ScanDone(host, "T") && host.ActiveJobs == 0));
        Assert.True(Fs.WaitUntil(() => host.GetNodes([big.Id], 64).SingleOrDefault()?.State == NodeState.Done));
        var bigAfter = host.GetNodes([big.Id], 64).Single();
        Assert.True(bigAfter.TotalFiles >= 1);
        Assert.True(bigAfter.TotalFiles < 3001);
        Assert.Contains(bigAfter.Children, c => c.Name == "d0");

        // Force close saves the journal even with everything idle.
        host.ForceClose();
        Assert.True(File.Exists(new Journal(tmp.Sub("data")).Path));
    }

    [Fact]
    public void Resume_journaled_job_deletes_the_tree_and_is_flagged_resumed()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("resume-me");
        Fs.MakeTree(root);
        var dataDir = tmp.Sub("data");
        var journal = new Journal(dataDir);
        journal.Save([
            new JobRecord
            {
                Id = 5,
                Path = root,
                Mode = DeleteMode.Permanent,
                IsDir = true,
                ExpectedBytes = 1,
                ExpectedFiles = 1,
                ExpectedComplete = false,
                BytesDeleted = 0,
                FilesDeleted = 0,
                DirsDeleted = 0,
                ErrorCount = 0,
                Errors = [],
                Status = JobStatus.Running,
                StartedMs = 1,
                UpdatedMs = 1,
                ElapsedMs = 250,
                Resumed = false,
                Message = null,
            },
            new JobRecord
            {
                Id = 2,
                Path = @"D:\finished\earlier",
                Mode = DeleteMode.Permanent,
                IsDir = true,
                ExpectedBytes = null,
                ExpectedFiles = null,
                ExpectedComplete = true,
                BytesDeleted = 3,
                FilesDeleted = 1,
                DirsDeleted = 1,
                ErrorCount = 0,
                Errors = [],
                Status = JobStatus.Done,
                StartedMs = 1,
                UpdatedMs = 2,
                ElapsedMs = 9,
                Resumed = false,
                Message = null,
            },
        ]);

        using var host = new EngineHost(dataDir);
        host.ResumeJournaledJobs();
        Assert.True(Fs.WaitUntil(() => host.GetJobs().All(j => !j.Status.IsActive())));
        Assert.False(Directory.Exists(root));

        var jobs = host.GetJobs();
        Assert.Equal(2, jobs.Count);
        var resumed = jobs.Single(j => j.Id == "5");
        Assert.True(resumed.Resumed);
        Assert.Equal(JobStatus.Done, resumed.Status);
        Assert.True(resumed.ElapsedMs >= 250);
        Assert.Null(resumed.NodeId);
        var old = jobs.Single(j => j.Id == "2");
        Assert.False(old.Resumed);
        Assert.Equal(JobStatus.Done, old.Status);

        // New ids continue after the highest journaled id.
        Fs.Write(tmp.Sub("vol", "a", "x.bin"), 1);
        var rootId = host.StartScanAt('T', tmp.Sub("vol"));
        Assert.True(Fs.WaitUntil(() => ScanDone(host, "T")));
        var a = host.GetNodes([rootId], 64)[0].Children[0];
        var next = host.StartDelete(new DeleteTarget(a.Id), DeleteMode.Permanent);
        Assert.Equal("6", next);
        Assert.True(Fs.WaitUntil(() => host.GetJobs().All(j => !j.Status.IsActive())));
    }

    [Fact]
    public void Finished_jobs_are_pruned_to_fifty()
    {
        using var tmp = new TempDir();
        var dataDir = tmp.Sub("data");
        var records = Enumerable.Range(1, 60).Select(i => new JobRecord
        {
            Id = (ulong)i,
            Path = $@"D:\old\{i}",
            Mode = DeleteMode.Permanent,
            IsDir = true,
            ExpectedBytes = null,
            ExpectedFiles = null,
            ExpectedComplete = true,
            BytesDeleted = 0,
            FilesDeleted = 0,
            DirsDeleted = 0,
            ErrorCount = 0,
            Errors = [],
            Status = JobStatus.Done,
            StartedMs = (ulong)i,
            UpdatedMs = (ulong)i,
            ElapsedMs = 1,
            Resumed = false,
            Message = null,
        }).ToList();
        new Journal(dataDir).Save(records);

        using var host = new EngineHost(dataDir);
        host.ResumeJournaledJobs();
        Assert.Equal(60, host.GetJobs().Count); // loading never prunes

        Fs.Write(tmp.Sub("vol", "a", "x.bin"), 1);
        var rootId = host.StartScanAt('T', tmp.Sub("vol"));
        Assert.True(Fs.WaitUntil(() => ScanDone(host, "T")));
        var a = host.GetNodes([rootId], 64)[0].Children[0];
        host.StartDelete(new DeleteTarget(a.Id), DeleteMode.Permanent);
        Assert.True(Fs.WaitUntil(() => host.GetJobs().All(j => !j.Status.IsActive())));

        // 61 finished jobs → oldest dropped first, down to 50.
        var jobs = host.GetJobs();
        Assert.Equal(50, jobs.Count);
        Assert.DoesNotContain(jobs, j => j.Id == "1");
        Assert.Contains(jobs, j => j.Id == "61");
    }
}
