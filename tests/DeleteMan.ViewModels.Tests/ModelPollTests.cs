using DeleteMan.Engine;
using DeleteMan.ViewModels.Tests.TestSupport;

namespace DeleteMan.ViewModels.Tests;

/// <summary>Poll bookkeeping (model.ts applyPoll / staleFileLists / expandedNodeIds / lettersWithUsedRow).</summary>
public class ModelPollTests
{
    [Fact]
    public void Expanded_node_ids_are_unique_and_letters_with_rows_are_reported()
    {
        var m = new Model();
        Assert.Empty(m.ExpandedNodeIds());
        Assert.Empty(m.LettersWithUsedRow());
        m.Chains["C"] = [Chain.UsedEntry("C:0"), new ChainEntry(EntryKind.Dir, "C:1", "C:1"), new ChainEntry(EntryKind.Files, "C:1", "files")];
        m.Chains["D"] = [];
        m.Chains["E"] = [Chain.UsedEntry("E:0")];
        Assert.Equal(["C:0", "C:1", "E:0"], m.ExpandedNodeIds());
        Assert.Equal(["C", "E"], m.LettersWithUsedRow());
    }

    [Fact]
    public void Apply_poll_replaces_drives_jobs_scans_and_live_nodes()
    {
        var m = new Model();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), new ChainEntry(EntryKind.Dir, "C:1", "C:1")];
        var drives = new[] { Make.Drive() };
        var jobs = new[] { Make.Job() };
        var nodes = new[] { Make.Node("C:0", @"C:\", @"C:\", isRoot: true), Make.Node("C:1", "Users", @"C:\Users") };
        var scans = new[] { Make.Scan() };
        m.ApplyPoll(drives, jobs, nodes, ["C:0", "C:1"], scans);

        Assert.Same(drives[0], m.Drives[0]);
        Assert.Same(jobs[0], m.Jobs[0]);
        Assert.Equal(2, m.Nodes.Count);
        Assert.Equal(ScanState.Scanning, m.Scans["C"].Status);
        Assert.Equal(2, m.Chains["C"].Count);
    }

    [Fact]
    public void Apply_poll_drops_deleted_and_missing_nodes_and_prunes_chains()
    {
        var m = new Model();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), new ChainEntry(EntryKind.Dir, "C:1", "C:1"), new ChainEntry(EntryKind.Dir, "C:5", "C:5")];
        m.Nodes["C:5"] = Make.Node("C:5", "steve", @"C:\Users\steve");
        m.Nodes["X:9"] = Make.Node("X:9", "stale", @"X:\stale");

        // C:1 comes back as deleted, C:5 does not come back at all, X:9 was not requested.
        m.ApplyPoll(
            [Make.Drive()],
            [],
            [Make.Node("C:0", @"C:\", @"C:\", isRoot: true), Make.Node("C:1", "Users", @"C:\Users", state: NodeState.Deleted)],
            ["C:0", "C:1", "C:5"],
            [Make.Scan()]);

        Assert.True(m.Nodes.ContainsKey("C:0"));
        Assert.False(m.Nodes.ContainsKey("C:1"));
        Assert.False(m.Nodes.ContainsKey("C:5"));
        Assert.True(m.Nodes.ContainsKey("X:9"));
        Assert.Equal([Chain.UsedEntry("C:0")], m.Chains["C"]);
    }

    [Fact]
    public void Apply_poll_forgets_chains_of_drives_that_disappeared()
    {
        var m = new Model();
        m.Chains["C"] = [Chain.UsedEntry("C:0")];
        m.Chains["E"] = [Chain.UsedEntry("E:0")];
        m.ApplyPoll([Make.Drive("C")], [], [Make.Node("C:0", @"C:\", @"C:\", isRoot: true)], ["C:0", "E:0"], []);
        Assert.True(m.Chains.ContainsKey("C"));
        Assert.False(m.Chains.ContainsKey("E"));
    }

    [Fact]
    public void Reconcile_closes_a_file_row_once_its_folder_no_longer_lists_the_file()
    {
        var m = Make.Fixture();
        var files = new ChainEntry(EntryKind.Files, "C:1", "files");
        m.Chains["C"] = [Chain.UsedEntry("C:0"), new ChainEntry(EntryKind.Dir, "C:1", "C:1"), files, new ChainEntry(EntryKind.File, "C:1", "f:a.bin")];

        // No list yet: nothing to judge by. The rows keep their folders' lists wanted.
        Assert.Equal(["C:0", "C:1"], m.StaleFileLists());
        Assert.False(m.ReconcileFileRows());
        Assert.Equal(4, m.Chains["C"].Count);

        m.Files["C:1"] = [Make.File("a.bin", 60), Make.File("b.bin", 40), Make.File("c.bin", 30)];
        Assert.False(m.ReconcileFileRows());

        // The file was deleted: its row goes; the files row stays, since two files still show.
        m.Files["C:1"] = [Make.File("b.bin", 40), Make.File("c.bin", 30)];
        Assert.True(m.ReconcileFileRows());
        Assert.Equal([Chain.UsedEntry("C:0"), new ChainEntry(EntryKind.Dir, "C:1", "C:1"), files], m.Chains["C"]);
        Assert.False(m.ReconcileFileRows());
    }

    [Fact]
    public void Reconcile_turns_a_files_row_into_the_file_row_when_the_folder_amounts_to_one_file()
    {
        var m = Make.Fixture();
        var dir = new ChainEntry(EntryKind.Dir, "C:1", "C:1");
        var files = new ChainEntry(EntryKind.Files, "C:1", "files");
        m.Chains["C"] = [Chain.UsedEntry("C:0"), dir, files];
        Assert.False(m.ReconcileFileRows()); // no list yet

        // One file: the files row becomes its row.
        m.Files["C:1"] = [Make.File("notes.txt", 20)];
        Assert.True(m.ReconcileFileRows());
        Assert.Equal([Chain.UsedEntry("C:0"), dir, new ChainEntry(EntryKind.File, "C:1", "f:notes.txt")], m.Chains["C"]);

        // One file that shows plus folded ones: the same, and whatever was open under the files row goes with it.
        m.Chains["C"] = [Chain.UsedEntry("C:0"), dir, files, new ChainEntry(EntryKind.Small, "C:1", "small")];
        m.Files["C:1"] = [Make.File("big.bin", 100_000), Make.File("tiny", 1)];
        Assert.True(m.ReconcileFileRows());
        Assert.Equal([Chain.UsedEntry("C:0"), dir, new ChainEntry(EntryKind.File, "C:1", "f:big.bin")], m.Chains["C"]);

        // Several files that show: the files row stays.
        m.Chains["C"] = [Chain.UsedEntry("C:0"), dir, files];
        m.Files["C:1"] = [Make.File("a.bin", 60), Make.File("b.bin", 40)];
        Assert.False(m.ReconcileFileRows());
    }

    [Fact]
    public void Stale_file_lists_track_shown_folders_and_files_rows_by_signature()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), new ChainEntry(EntryKind.Dir, "C:1", "C:1"), new ChainEntry(EntryKind.Files, "C:1", "files")];

        // The root (2 loose files) and Users (1) are shown, so both lists are wanted; the files row is Users'.
        Assert.Equal(["C:0", "C:1"], m.StaleFileLists());
        Assert.Equal("1:20|", m.FilesSig["C:1"]);
        Assert.Empty(m.StaleFileLists()); // nothing changed

        // A finished job changes the signature (files may have been deleted), a running one does not.
        m.Jobs = [Make.Job("7", status: JobStatus.Running)];
        Assert.Empty(m.StaleFileLists());
        m.Jobs = [Make.Job("7", status: JobStatus.Done), Make.Job("8", status: JobStatus.Cancelled)];
        Assert.Equal(["C:0", "C:1"], m.StaleFileLists());
        Assert.Equal("1:20|7,8", m.FilesSig["C:1"]);

        // The folder's own counts changing re-fetches too.
        m.Nodes["C:1"] = m.Nodes["C:1"] with { OwnFiles = 2, OwnBytes = 25 };
        Assert.Equal(["C:1"], m.StaleFileLists());

        // An unknown node still has a signature ("?") and is fetched once.
        m.Chains["C"] = [Chain.UsedEntry("C:0"), new ChainEntry(EntryKind.Dir, "C:9", "C:9"), new ChainEntry(EntryKind.Files, "C:9", "files")];
        m.Files["C:1"] = [Make.File("a.bin", 1)];
        Assert.Equal(["C:9"], m.StaleFileLists());
        Assert.False(m.Files.ContainsKey("C:1"), "closed rows drop their lists");
        Assert.False(m.FilesSig.ContainsKey("C:1"));
        Assert.StartsWith("?|", m.FilesSig["C:9"]);
    }

    [Fact]
    public void Node_requests_widen_the_child_window_of_folders_with_leftover_pages_open()
    {
        var m = Make.Fixture();
        m.Drives = [Make.Drive("C"), Make.Drive("D")];
        m.Chains["C"] = [Chain.UsedEntry("C:0"), new ChainEntry(EntryKind.Dir, "C:1", "C:1"), new ChainEntry(EntryKind.Other, "C:1", "other"), new ChainEntry(EntryKind.Other, "C:1", "other")];
        m.Chains["D"] = [Chain.UsedEntry("D:0"), new ChainEntry(EntryKind.Other, "D:0", "other"), new ChainEntry(EntryKind.Dir, "D:3", "D:3"), new ChainEntry(EntryKind.Files, "D:3", "files")];
        // One window of 64 per row of a node, plus one per open page of its leftover; files rows add nothing.
        Assert.Equal(
            [new NodeRequest("C:0", 64), new NodeRequest("C:1", 192), new NodeRequest("D:0", 128), new NodeRequest("D:3", 64)],
            m.NodeRequests());
        Assert.Equal(["C:0", "C:1", "D:0", "D:3"], m.ExpandedNodeIds());
    }

    [Fact]
    public void Apply_poll_keeps_the_previous_objects_when_nothing_changed()
    {
        var m = new Model();
        m.Chains["C"] = [Chain.UsedEntry("C:0")];
        var drives = new[] { Make.Drive() };
        var jobs = new[] { Make.Job() };
        var node = Make.Node("C:0", @"C:\", @"C:\", isRoot: true, total: 500, children: [Make.Child("C:1", "Users", 300, 10)]);
        var scan = Make.Scan();
        m.ApplyPoll(drives, jobs, [node], ["C:0"], [scan]);

        // The next poll brings the same content as fresh objects (every poll does); the model keeps the old ones,
        // so rows built from them come out equal and the UI has nothing to touch.
        m.ApplyPoll(
            [Make.Drive()],
            [Make.Job()],
            [Make.Node("C:0", @"C:\", @"C:\", isRoot: true, total: 500, children: [Make.Child("C:1", "Users", 300, 10)])],
            ["C:0"],
            [Make.Scan()]);
        Assert.Same(drives, m.Drives);
        Assert.Same(jobs, m.Jobs);
        Assert.Same(node, m.Nodes["C:0"]);
        Assert.Same(scan, m.Scans["C"]);

        // Any difference replaces the object: a child that grew, a job that progressed, a drive with less free space.
        var grown = Make.Node("C:0", @"C:\", @"C:\", isRoot: true, total: 500, children: [Make.Child("C:1", "Users", 301, 10)]);
        var progressed = new[] { Make.Job(bytes: 260) };
        var fuller = new[] { Make.Drive(free: 399) };
        m.ApplyPoll(fuller, progressed, [grown], ["C:0"], [Make.Scan(dirs: 1235)]);
        Assert.Same(fuller, m.Drives);
        Assert.Same(progressed, m.Jobs);
        Assert.Same(grown, m.Nodes["C:0"]);
        Assert.Equal(1235UL, m.Scans["C"].DirsScanned);
    }

    [Fact]
    public void Node_and_job_content_comparisons_look_inside_their_lists()
    {
        var node = Make.Node("C:0", @"C:\", @"C:\", children: [Make.Child("C:1", "Users", 300, 10)]);
        Assert.True(Model.SameContent(node, Make.Node("C:0", @"C:\", @"C:\", children: [Make.Child("C:1", "Users", 300, 10)])));
        Assert.False(Model.SameContent(node, Make.Node("C:0", @"C:\", @"C:\", children: [Make.Child("C:1", "Users", 300, 11)])));
        Assert.False(Model.SameContent(node, Make.Node("C:0", @"C:\", @"C:\", children: [])));
        Assert.False(Model.SameContent(node, node with { State = NodeState.Scanning }));

        var locked = new JobError(@"C:\x", 32, "locked");
        var job = Make.Job(errors: [locked]);
        Assert.True(Model.SameJobs([job], [Make.Job(errors: [new JobError(@"C:\x", 32, "locked")])]));
        Assert.False(Model.SameJobs([job], [Make.Job(errors: [new JobError(@"C:\y", 32, "locked")])]));
        Assert.False(Model.SameJobs([job], [Make.Job(errors: [locked], bytes: 251)]));
        Assert.False(Model.SameJobs([job], []));
        Assert.True(Model.SameJobs([], []));
    }
}
