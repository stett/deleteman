using DeleteMan.Engine.Tests.TestSupport;

namespace DeleteMan.Engine.Tests;

/// <summary>Port of the scan.rs tests plus unit tests for the arena and the gate.</summary>
public class ScannerTests
{
    [Fact]
    public void Bfs_sizes_are_exact()
    {
        using var tmp = new TempDir();
        var root = tmp.Path;
        Directory.CreateDirectory(Path.Combine(root, "a", "aa"));
        Directory.CreateDirectory(Path.Combine(root, "a", "ab"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        Fs.Write(Path.Combine(root, "top.bin"), 10);
        Fs.Write(Path.Combine(root, "a", "x.bin"), 100);
        Fs.Write(Path.Combine(root, "a", "aa", "y.bin"), 1000);
        Fs.Write(Path.Combine(root, "a", "ab", "z.bin"), 10_000);
        Fs.Write(Path.Combine(root, "b", "w.bin"), 100_000);

        var scan = DriveScan.WithRoot('T', root);
        var gate = new Gate();
        Assert.Equal(ScanState.Idle, scan.Status);
        scan.RunBfs(gate, 0);

        Assert.Equal(ScanState.Done, scan.Status);
        var view = scan.NodeView(0, 64)!;
        Assert.Equal("T:0", view.Id);
        Assert.True(view.IsRoot);
        Assert.Equal(root, view.Path);
        Assert.Equal(root, view.Name);
        Assert.Equal(111_110UL, view.TotalBytes);
        Assert.Equal(5UL, view.TotalFiles);
        Assert.Equal(5UL, view.TotalDirs);
        Assert.Equal(10UL, view.OwnBytes);
        Assert.Equal(1UL, view.OwnFiles);
        Assert.Equal(NodeState.Done, view.State);
        Assert.Null(view.ErrorCode);
        Assert.Null(view.DeletingJobId);
        Assert.Equal(3, view.Children.Count);
        Assert.Equal("b", view.Children[0].Name);
        Assert.Equal(100_000UL, view.Children[0].TotalBytes);
        Assert.Equal(1UL, view.Children[0].TotalFiles);
        Assert.Equal("a", view.Children[1].Name);
        Assert.Equal(11_100UL, view.Children[1].TotalBytes);
        Assert.Equal("empty", view.Children[2].Name);
        Assert.Equal(0UL, view.OtherCount);
        Assert.Equal(111_110UL, scan.Accounted);
        Assert.Equal(3U, scan.Depth);
        Assert.Equal(6UL, scan.DirsScanned);

        var status = scan.StatusView();
        Assert.Equal("T", status.Drive);
        Assert.Equal(ScanState.Done, status.Status);
        Assert.Equal(3U, status.Depth);
        Assert.Equal(6UL, status.DirsScanned);
        Assert.Equal(111_110UL, status.AccountedBytes);

        var a = DriveScan.ParseNodeId(view.Children[1].Id)!.Value.Index;
        var aView = scan.NodeView(a, 1)!;
        Assert.False(aView.IsRoot);
        Assert.Equal("a", aView.Name);
        Assert.Single(aView.Children);
        Assert.Equal("ab", aView.Children[0].Name);
        Assert.Equal(1UL, aView.OtherCount);
        Assert.Equal(1000UL, aView.OtherBytes);
        Assert.Equal(Path.Combine(root, "a"), aView.Path);
        Assert.Equal(Path.Combine(root, "a"), scan.PathOf(a).Display);

        // Removing "b" takes its totals out of the root.
        var b = DriveScan.ParseNodeId(view.Children[0].Id)!.Value.Index;
        scan.RemoveNode(b);
        view = scan.NodeView(0, 64)!;
        Assert.Equal(11_110UL, view.TotalBytes);
        Assert.Equal(4UL, view.TotalDirs);
        Assert.Null(scan.NodeView(b, 64));
        Assert.Equal(2, view.Children.Count);
        Assert.Equal(11_110UL, scan.Accounted);

        // Resetting "a" and rescanning it restores the same numbers.
        scan.ResetSubtree(a);
        Assert.Equal(10UL, scan.NodeView(0, 64)!.TotalBytes);
        Assert.Equal(NodeState.Pending, scan.Node(a)!.State);
        scan.RunBfs(gate, a);
        Assert.Equal(11_110UL, scan.NodeView(0, 64)!.TotalBytes);
        Assert.Equal(11_100UL, scan.NodeView(a, 64)!.TotalBytes);
        Assert.Equal(ScanState.Done, scan.Status);

        var files = scan.ListFiles(a, 10);
        Assert.Single(files);
        Assert.Equal("x.bin", files[0].Name);
        Assert.Equal(100UL, files[0].Size);

        // Subtracting a deleted loose file adjusts own and recursive totals.
        scan.SubtractFile(a, 100);
        Assert.Equal(11_000UL, scan.NodeView(a, 64)!.TotalBytes);
        Assert.Equal(0UL, scan.NodeView(a, 64)!.OwnBytes);
        Assert.Equal(0UL, scan.NodeView(a, 64)!.OwnFiles);
        Assert.Equal(11_010UL, scan.NodeView(0, 64)!.TotalBytes);
        Assert.Equal(11_010UL, scan.Accounted);
    }

    [Fact]
    public void Node_ids_parse()
    {
        Assert.Equal(new NodeRef('C', 0), DriveScan.ParseNodeId("C:0"));
        Assert.Equal(new NodeRef('D', 42), DriveScan.ParseNodeId("d:42"));
        Assert.Null(DriveScan.ParseNodeId("CD:1"));
        Assert.Null(DriveScan.ParseNodeId("C:x"));
        Assert.Null(DriveScan.ParseNodeId("C"));
        Assert.Null(DriveScan.ParseNodeId(""));
        Assert.Null(DriveScan.ParseNodeId("1:0"));
        var scan = DriveScan.WithRoot('Q', @"C:\Windows");
        Assert.Equal("Q:0", scan.NodeId(0));
        Assert.Equal("Q:17", scan.NodeId(17));
    }

    [Fact]
    public void List_files_is_largest_first_and_capped()
    {
        using var tmp = new TempDir();
        Fs.Write(tmp.Sub("small.bin"), 1);
        Fs.Write(tmp.Sub("big.bin"), 300);
        Fs.Write(tmp.Sub("mid.bin"), 20);
        Fs.Write(tmp.Sub("mid2.bin"), 20);
        Directory.CreateDirectory(tmp.Sub("dir"));
        var scan = DriveScan.WithRoot('T', tmp.Path);
        scan.RunBfs(new Gate(), 0);
        var files = scan.ListFiles(0, 3);
        Assert.Equal(["big.bin", "mid.bin", "mid2.bin"], files.Select(f => f.Name));
        Assert.Equal(300UL, files[0].Size);
        Assert.True((files[0].Attrs & 0x10) == 0);
    }

    [Fact]
    public void Reparse_points_are_never_followed_or_counted()
    {
        using var tmp = new TempDir();
        var outside = tmp.Sub("outside");
        Fs.Write(Path.Combine(outside, "huge.bin"), 50_000);
        var root = tmp.Sub("root");
        Fs.Write(Path.Combine(root, "own.bin"), 10);
        Fs.CreateJunction(Path.Combine(root, "link"), outside);

        var scan = DriveScan.WithRoot('T', root);
        scan.RunBfs(new Gate(), 0);
        var view = scan.NodeView(0, 64)!;
        Assert.Equal(10UL, view.TotalBytes);
        Assert.Equal(0UL, view.TotalDirs);
        Assert.Empty(view.Children);
        Assert.Equal(1UL, scan.DirsScanned);
    }

    [Fact]
    public void Unreadable_directory_is_reported_as_an_error_node()
    {
        using var tmp = new TempDir();
        var root = tmp.Path;
        var denied = Path.Combine(root, "denied");
        Directory.CreateDirectory(denied);
        Fs.Write(Path.Combine(denied, "secret.bin"), 100);
        Fs.Write(Path.Combine(root, "ok.bin"), 5);

        // Deny listing to ourselves via an explicit ACL; restore afterwards so the temp dir can be removed.
        var info = new DirectoryInfo(denied);
        var acl = info.GetAccessControl();
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        acl.AddAccessRule(rule);
        info.SetAccessControl(acl);
        try
        {
            var scan = DriveScan.WithRoot('T', root);
            scan.RunBfs(new Gate(), 0);
            var view = scan.NodeView(0, 64)!;
            Assert.Equal(ScanState.Done, scan.Status);
            Assert.Equal(5UL, view.TotalBytes);
            var child = Assert.Single(view.Children);
            Assert.Equal("denied", child.Name);
            Assert.Equal(NodeState.Error, child.State);
            Assert.Equal(5U, child.ErrorCode); // ERROR_ACCESS_DENIED
            var deniedView = scan.NodeView(DriveScan.ParseNodeId(child.Id)!.Value.Index, 64)!;
            Assert.Equal(NodeState.Error, deniedView.State);
            Assert.Equal(5U, deniedView.ErrorCode);
        }
        finally
        {
            acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
    }

    [Fact]
    public void Refresh_dir_matches_fresh_scan()
    {
        using var tmp = new TempDir();
        var root = tmp.Path;
        Directory.CreateDirectory(Path.Combine(root, "a", "aa"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        Fs.Write(Path.Combine(root, "a", "x.bin"), 100);
        Fs.Write(Path.Combine(root, "a", "aa", "y.bin"), 1000);
        Fs.Write(Path.Combine(root, "b", "w.bin"), 500);

        var gate = new Gate();
        var scan = DriveScan.WithRoot('T', root);
        scan.RunBfs(gate, 0);
        Assert.Equal(1600UL, scan.NodeView(0, 64)!.TotalBytes);

        // External changes: add a loose file, resize one, remove another; add a populated subtree; drop a subdir.
        Fs.Write(Path.Combine(root, "a", "z.bin"), 7);
        Fs.Write(Path.Combine(root, "a", "x.bin"), 250);
        File.Delete(Path.Combine(root, "b", "w.bin"));
        Directory.CreateDirectory(Path.Combine(root, "a", "newdir", "deep"));
        Fs.Write(Path.Combine(root, "a", "newdir", "f.bin"), 42);
        Fs.Write(Path.Combine(root, "a", "newdir", "deep", "g.bin"), 8);
        Directory.Delete(Path.Combine(root, "a", "aa"), recursive: true);

        var a = scan.FindNode(["A"]); // case-insensitive match
        var b = scan.FindNode(["b"]);
        Assert.NotEqual(0U, a);
        Assert.NotEqual(0U, b);
        Assert.NotEqual(a, b);
        Assert.Equal(0U, scan.FindNode(["nope"]));                  // unknown component: deepest ancestor is the root
        Assert.Equal(a, scan.FindNode(["a", "does-not-exist"]));    // deepest existing ancestor
        scan.RefreshDir(gate, a);
        scan.RefreshDir(gate, b);

        // A fresh scan of the modified tree is the source of truth; the incremental refresh must match it.
        var fresh = DriveScan.WithRoot('T', root);
        fresh.RunBfs(new Gate(), 0);
        var v = scan.NodeView(0, 64)!;
        var fv = fresh.NodeView(0, 64)!;
        Assert.True(v.TotalBytes == fv.TotalBytes, "bytes drift");
        Assert.True(v.TotalFiles == fv.TotalFiles, "files drift");
        Assert.True(v.TotalDirs == fv.TotalDirs, "dirs drift");
        Assert.True(scan.Accounted == fresh.Accounted, "accounted drift");
        Assert.Equal(ScanState.Done, scan.Status);
    }

    [Fact]
    public void Cancelled_scan_reports_cancelled()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 50; i++)
        {
            Fs.Write(tmp.Sub($"d{i}", "f.bin"), 1);
        }
        var scan = DriveScan.WithRoot('T', tmp.Path);
        scan.Cancel();
        scan.RunBfs(new Gate(), 0);
        Assert.Equal(ScanState.Cancelled, scan.Status);
    }

    [Fact]
    public void Arena_is_append_only_and_readable_while_pushing()
    {
        var arena = new Arena<string>();
        Assert.Equal(0, arena.Count);
        Assert.Null(arena.TryGet(0));
        var first = arena.Push("zero");
        Assert.Equal(0, first);
        Assert.Equal("zero", arena[0]);

        const int perThread = 20_000;
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                var idx = arena.Push($"{t}:{i}");
                Assert.Equal($"{t}:{i}", arena[idx]);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(1 + 8 * perThread, arena.Count);
        var seen = new HashSet<string>();
        for (var i = 0; i < arena.Count; i++)
        {
            Assert.True(seen.Add(arena[i]));
        }
        Assert.Null(arena.TryGet(arena.Count));
        Assert.Null(arena.TryGet(-1));
    }

    [Fact]
    public void Gate_blocks_entries_while_paused_and_drains_in_flight_work()
    {
        var gate = new Gate();
        Assert.False(gate.IsPaused);
        using (gate.Enter())
        {
            // An in-flight listing: pause must wait for it to finish.
            var paused = new ManualResetEventSlim();
            var drained = new ManualResetEventSlim();
            var pauser = new Thread(() =>
            {
                paused.Set();
                gate.PauseAndDrain();
                drained.Set();
            });
            pauser.Start();
            paused.Wait(TestContext.Current.CancellationToken);
            Thread.Sleep(100);
            Assert.False(drained.IsSet);
            Assert.True(gate.IsPaused);
        }
        Assert.True(Fs.WaitUntil(() => gate.IsPaused, TimeSpan.FromSeconds(5)));

        // While paused a new Enter blocks until Resume.
        var entered = new ManualResetEventSlim();
        var enterer = new Thread(() =>
        {
            using (gate.Enter())
            {
                entered.Set();
            }
        });
        enterer.Start();
        Thread.Sleep(100);
        Assert.False(entered.IsSet);
        gate.Resume();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(gate.IsPaused);
        enterer.Join();
    }

    [Fact]
    public void Path_of_a_node_matches_the_component_builder()
    {
        using var tmp = new TempDir();
        var root = tmp.Path;
        Directory.CreateDirectory(Path.Combine(root, "a", "aa"));
        Fs.Write(Path.Combine(root, "a", "aa", "y.bin"), 10);
        var scan = DriveScan.WithRoot('T', root);
        scan.RunBfs(new Gate(), 0);

        var a = DriveScan.ParseNodeId(scan.NodeView(0, 64)!.Children.Single().Id)!.Value.Index;
        var aa = DriveScan.ParseNodeId(scan.NodeView(a, 64)!.Children.Single().Id)!.Value.Index;
        Assert.Equal(WidePath.FromComponents(root).Wide, scan.PathOf(0).Wide);
        Assert.Equal(WidePath.FromComponents(root, "a").Wide, scan.PathOf(a).Wide);
        Assert.Equal(WidePath.FromComponents(root, "a", "aa").Wide, scan.PathOf(aa).Wide);
        Assert.Equal(Path.Combine(root, "a", "aa"), scan.PathOf(aa).Display);

        // A whole-drive scan names its root "X:", which is a volume root only with the trailing backslash.
        Assert.Equal(@"\\?\C:\", new DriveScan('C').PathOf(0).Wide);
        Assert.Equal(WidePath.FromComponents("C:").Wide, new DriveScan('C').PathOf(0).Wide);
    }
}
