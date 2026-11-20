using DeleteMan.Engine.Tests.TestSupport;

namespace DeleteMan.Engine.Tests;

/// <summary>Port of the delete.rs tests: parallel, cancellable, resumable deletion with POSIX semantics.</summary>
public class DeleterTests
{
    private static Job JobFor(string path) =>
        new(1, WidePath.FromDisplay(path), DeleteMode.Permanent, isDir: true, node: null, expectedBytes: null, expectedFiles: null, expectedComplete: true);

    private static string Describe(Job job) => string.Join("; ", job.Errors.Select(e => $"{e.Path} — {e.Message} ({e.Code})"));

    [Fact]
    public void Deletes_readonly_files_and_nested_dirs()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("tree");
        var (files, dirs) = Fs.MakeTree(root);
        using var pool = new DeletePool("test-del", 4);
        var job = JobFor(root);
        Deleter.Run(job, pool);
        Assert.True(job.Status == JobStatus.Done, Describe(job));
        Assert.False(Directory.Exists(root));
        Assert.Equal((ulong)files, job.FilesDeleted);
        Assert.Equal((ulong)dirs + 1, job.DirsDeleted);
        Assert.True(job.BytesDeleted > 0);
        Assert.Equal(0UL, job.ErrorCount);
        Assert.False(job.IsCancelRequested);
    }

    [Fact]
    public void Deletes_paths_longer_than_max_path()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("long");
        var deep = root;
        for (var i = 0; i < 6; i++)
        {
            deep = Path.Combine(deep, $"segment-{i}-{new string('x', 50)}");
        }
        Directory.CreateDirectory(deep);
        Assert.True(deep.Length > 300);
        File.WriteAllBytes(Path.Combine(deep, "leaf.bin"), "hello"u8.ToArray());

        using var pool = new DeletePool("test-del", 2);
        var job = JobFor(root);
        Deleter.Run(job, pool);
        Assert.True(job.Status == JobStatus.Done, Describe(job));
        Assert.False(Directory.Exists(root));
        Assert.Equal(1UL, job.FilesDeleted);
        Assert.Equal(7UL, job.DirsDeleted);
    }

    [Fact]
    public void Junction_is_removed_but_target_survives()
    {
        using var tmp = new TempDir();
        var target = tmp.Sub("target");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "CANARY.txt"), "still here"u8.ToArray());
        var root = tmp.Sub("tree");
        Directory.CreateDirectory(Path.Combine(root, "links"));
        File.WriteAllBytes(Path.Combine(root, "links", "a.bin"), "a"u8.ToArray());
        var link = Path.Combine(root, "links", "to-target");
        Fs.CreateJunction(link, target);
        Assert.True(File.Exists(Path.Combine(link, "CANARY.txt")));

        using var pool = new DeletePool("test-del", 2);
        var job = JobFor(root);
        Deleter.Run(job, pool);
        Assert.True(job.Status == JobStatus.Done, Describe(job));
        Assert.False(Directory.Exists(root));
        Assert.Equal("still here"u8.ToArray(), File.ReadAllBytes(Path.Combine(target, "CANARY.txt")));
        Assert.Equal(1UL, job.FilesDeleted);
        // the junction entry, "links" and the root itself
        Assert.Equal(3UL, job.DirsDeleted);
    }

    [Fact]
    public void Locked_file_is_reported_and_rest_is_deleted()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("tree");
        Fs.MakeTree(root);
        var locked = Path.Combine(root, "d0", "s0", "held.bin");
        File.WriteAllBytes(locked, "locked"u8.ToArray());
        using var pool = new DeletePool("test-del", 4);

        // Hold the file open without FILE_SHARE_DELETE, like a running program would.
        using (var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var job = JobFor(root);
            Deleter.Run(job, pool);
            Assert.Equal(JobStatus.DoneWithErrors, job.Status);
            // Only the root cause is reported; the parents that stay behind because of it are not errors.
            Assert.True(job.ErrorCount == 1, Describe(job));
            Assert.True(File.Exists(locked));
            Assert.True(Directory.Exists(Path.Combine(root, "d0")));
            Assert.False(Directory.Exists(Path.Combine(root, "d1")));
            Assert.False(Directory.Exists(Path.Combine(root, "empty")));
            var errs = job.Errors;
            Assert.Single(errs);
            Assert.EndsWith("held.bin", errs[0].Path);
            Assert.Equal(32U, errs[0].Code); // ERROR_SHARING_VIOLATION
            Assert.False(string.IsNullOrWhiteSpace(errs[0].Message));
        }

        var job2 = JobFor(root);
        Deleter.Run(job2, pool);
        Assert.True(job2.Status == JobStatus.Done, Describe(job2));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Cancelled_before_start_leaves_tree_intact()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("tree");
        Fs.MakeTree(root);
        using var pool = new DeletePool("test-del", 2);
        var job = JobFor(root);
        job.RequestCancel();
        Deleter.Run(job, pool);
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.True(File.Exists(Path.Combine(root, "d0", "s0", "f1.bin")));
        Assert.Equal(0UL, job.FilesDeleted);
    }

    [Fact]
    public void Single_file_job()
    {
        using var tmp = new TempDir();
        var file = tmp.Sub("big.bin");
        Fs.Write(file, 4096, 7);
        var job = new Job(2, WidePath.FromDisplay(file), DeleteMode.Permanent, isDir: false, node: null, expectedBytes: 4096, expectedFiles: 1, expectedComplete: true);
        using var pool = new DeletePool("test-del", 1);
        Deleter.Run(job, pool);
        Assert.Equal(JobStatus.Done, job.Status);
        Assert.False(File.Exists(file));
        Assert.Equal(4096UL, job.BytesDeleted);
        Assert.Equal(1UL, job.FilesDeleted);
    }

    [Fact]
    public void Already_gone_target_finishes_as_done()
    {
        using var tmp = new TempDir();
        var job = JobFor(tmp.Sub("never-there"));
        using var pool = new DeletePool("test-del", 1);
        Deleter.Run(job, pool);
        Assert.Equal(JobStatus.Done, job.Status);
        Assert.Equal(0UL, job.FilesDeleted);
    }

    [Fact]
    public void Elapsed_and_timestamps_advance_while_running_and_freeze_when_done()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("tree");
        Fs.MakeTree(root);
        var job = JobFor(root);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.True(job.StartedMs > 0);
        using var pool = new DeletePool("test-del", 2);
        Deleter.Run(job, pool);
        var elapsed = job.ElapsedMs;
        Thread.Sleep(30);
        Assert.Equal(elapsed, job.ElapsedMs);
        Assert.True(job.UpdatedMs >= job.StartedMs);
    }

    [Fact]
    public void View_exposes_the_same_numbers_as_the_job()
    {
        var job = new Job(42, WidePath.FromDisplay(@"D:\nowhere\x"), DeleteMode.Recycle, isDir: true, node: new NodeRef('D', 7), expectedBytes: 10, expectedFiles: 2, expectedComplete: false);
        var view = job.ToView();
        Assert.Equal("42", view.Id);
        Assert.Equal(@"D:\nowhere\x", view.Path);
        Assert.Equal(DeleteMode.Recycle, view.Mode);
        Assert.Equal(JobStatus.Queued, view.Status);
        Assert.Equal("D:7", view.NodeId);
        Assert.Equal(10UL, view.ExpectedBytes);
        Assert.Equal(2UL, view.ExpectedFiles);
        Assert.False(view.ExpectedComplete);
        Assert.False(view.Resumed);
        Assert.Null(view.Message);
        Assert.Empty(view.Errors);
    }

    /// <summary>Moves a tiny temp tree to the Recycle Bin (leaves one small item there), so it is opt-in.</summary>
    [Fact(Explicit = true)]
    public void Recycle_mode_moves_tree_to_recycle_bin()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("recycle-me");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllBytes(Path.Combine(root, "a.txt"), "a"u8.ToArray());
        File.WriteAllBytes(Path.Combine(root, "sub", "b.txt"), "b"u8.ToArray());
        var job = new Job(3, WidePath.FromDisplay(root), DeleteMode.Recycle, isDir: true, node: null, expectedBytes: 2, expectedFiles: 2, expectedComplete: true);
        using var pool = new DeletePool("test-del", 1);
        Deleter.Run(job, pool);
        Assert.True(job.Status == JobStatus.Done, $"message: {job.Message}");
        Assert.False(Directory.Exists(root));
        Assert.Equal(2UL, job.FilesDeleted);
    }

    [Fact]
    public void Recycle_of_a_missing_path_is_done_without_touching_the_shell()
    {
        using var tmp = new TempDir();
        var job = new Job(4, WidePath.FromDisplay(tmp.Sub("gone")), DeleteMode.Recycle, isDir: true, node: null, expectedBytes: null, expectedFiles: null, expectedComplete: true);
        using var pool = new DeletePool("test-del", 1);
        Deleter.Run(job, pool);
        Assert.Equal(JobStatus.Done, job.Status);
    }

    [Fact]
    public void Cancel_request_does_not_change_a_recycle_outcome()
    {
        // A Recycle Bin operation cannot be interrupted; a cancel flag must not turn a finished job into Cancelled.
        using var tmp = new TempDir();
        var job = new Job(5, WidePath.FromDisplay(tmp.Sub("gone")), DeleteMode.Recycle, isDir: true, node: null, expectedBytes: null, expectedFiles: null, expectedComplete: true);
        job.RequestCancel();
        using var pool = new DeletePool("test-del", 1);
        Deleter.Run(job, pool);
        Assert.Equal(JobStatus.Done, job.Status);
    }

    [Fact]
    public void Thread_count_is_twice_the_cores_clamped_to_8_32()
    {
        var n = DeletePool.DefaultThreadCount();
        Assert.InRange(n, 8, 32);
        Assert.Equal(Math.Clamp(Environment.ProcessorCount * 2, 8, 32), n);
    }
}
