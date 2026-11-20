using System.Collections.Concurrent;
using System.ComponentModel;
using DeleteMan.Engine.Recycle;

namespace DeleteMan.Engine;

/// <summary>
/// Parallel, cancellable, resumable deletion of directory trees.
/// </summary>
/// <remarks>
/// Strategy: open each directory once, delete its files through relative NT opens with POSIX delete semantics, then
/// recurse into its subdirectories in parallel on the <see cref="DeletePool"/>, and finally remove the directory
/// itself. Every step is idempotent, so a job interrupted by a crash can simply be run again.
/// </remarks>
public static class Deleter
{
    [ThreadStatic]
    private static ulong[]? t_dirBuffer;

    /// <summary>Runs a job to completion on the calling thread (the permanent path fans out onto <paramref name="pool"/>).</summary>
    public static void Run(Job job, DeletePool pool) => job.Finish(Execute(job, pool));

    /// <summary>
    /// Runs the job and returns its outcome without publishing it, so a host can reconcile its own state before the
    /// job is seen as finished; the caller must pass the outcome to <see cref="Job.Finish"/>.
    /// </summary>
    internal static JobStatus Execute(Job job, DeletePool pool)
    {
        job.SetStatus(JobStatus.Running);
        try
        {
            if (job.Mode == DeleteMode.Permanent)
            {
                pool.Install(() => DeleteTarget(job, pool));
            }
            else
            {
                Recycle(job);
            }
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            job.Message = e.Message;
            return JobStatus.Failed;
        }
        catch (Exception)
        {
            // The Rust build caught panics here; any other exception is a bug, but the job must still end.
            job.Message = "internal error while deleting";
            return JobStatus.Failed;
        }
        // The Recycle Bin operation can't be interrupted, so a cancel request doesn't change its outcome.
        if (job.IsCancelRequested && job.Mode == DeleteMode.Permanent)
        {
            return JobStatus.Cancelled;
        }
        return job.ErrorCount > 0 ? JobStatus.DoneWithErrors : JobStatus.Done;
    }

    private static void DeleteTarget(Job job, DeletePool pool)
    {
        if (WinFs.AttributesOf(job.Path) is null)
        {
            return; // already gone (e.g. a resumed job that had finished its last step)
        }
        if (!job.IsDir)
        {
            WinFs.DeleteByPath(job.Path);
            job.AddFiles(1);
            job.AddBytes(job.ExpectedBytes ?? 0);
            return;
        }
        DeleteDir(job, job.Path, pool);
    }

    private static void Recycle(Job job)
    {
        if (WinFs.AttributesOf(job.Path) is null)
        {
            return;
        }
        RecycleBin.Delete(job.Path);
        job.AddFiles(job.ExpectedFiles ?? 0);
        job.AddBytes(job.ExpectedBytes ?? 0);
    }

    private static bool IsNotFound(int e) => e is 2 or 3;

    /// <summary>Deletes everything under <paramref name="path"/>, then <paramref name="path"/> itself. Returns true if the directory is gone.</summary>
    private static bool DeleteDir(Job job, WidePath path, DeletePool pool)
    {
        if (job.IsCancelRequested)
        {
            return false;
        }
        var openError = WinFs.TryOpenDirForEnum(path, out var dir);
        if (openError != 0)
        {
            if (IsNotFound(openError))
            {
                return true;
            }
            job.RecordError(path.Display, openError);
            return false;
        }

        var subdirs = new List<string>();
        var allOk = true;
        var cancelled = false;
        var seen = 0u;
        int enumError;
        using (dir)
        {
            var buffer = t_dirBuffer ??= new ulong[WinFs.DirBufferWords];
            enumError = WinFs.TryEnumerateDirHandle(dir!, buffer, (name, attrs, size) =>
            {
                seen++;
                if (seen % 256 == 0 && job.IsCancelRequested)
                {
                    cancelled = true;
                    return false;
                }
                var isDir = WinFs.IsDir(attrs);
                if (isDir && !WinFs.IsReparse(attrs))
                {
                    subdirs.Add(name.ToString());
                    return true;
                }
                var e = WinFs.TryDeleteChild(dir!, name, isDir);
                if (e == 0)
                {
                    if (isDir)
                    {
                        job.AddDirs(1);
                    }
                    else
                    {
                        job.AddFiles(1);
                        if (!WinFs.IsPlaceholder(attrs))
                        {
                            job.AddBytes(size);
                        }
                    }
                }
                else
                {
                    allOk = false;
                    job.RecordError(path.Join(name).Display, e);
                }
                return true;
            });
        }
        if (enumError != 0)
        {
            allOk = false;
            job.RecordError(path.Display, enumError);
        }
        job.Touch();
        if (cancelled || job.IsCancelRequested)
        {
            return false;
        }

        bool childrenOk;
        switch (subdirs.Count)
        {
            case 0:
                childrenOk = true;
                break;
            case 1:
                childrenOk = DeleteDir(job, path.Join(subdirs[0]), pool);
                break;
            default:
                var failed = 0;
                // One subdirectory at a time per worker: sibling subtrees differ wildly in size, so static chunking
                // would leave threads idle behind one huge folder.
                Parallel.ForEach(
                    Partitioner.Create(subdirs, EnumerablePartitionerOptions.NoBuffering),
                    new ParallelOptions { TaskScheduler = pool.Scheduler, MaxDegreeOfParallelism = pool.Threads },
                    name =>
                    {
                        if (!DeleteDir(job, path.Join(name), pool))
                        {
                            Volatile.Write(ref failed, 1);
                        }
                    });
                childrenOk = failed == 0;
                break;
        }
        if (!(allOk && childrenOk) || job.IsCancelRequested)
        {
            return false;
        }
        var deleteError = WinFs.TryDeleteByPath(path);
        if (deleteError == 0)
        {
            job.AddDirs(1);
            return true;
        }
        job.RecordError(path.Display, deleteError);
        return false;
    }
}
