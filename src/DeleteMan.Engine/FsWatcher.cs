using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeleteMan.Engine;

/// <summary>
/// One recursive filesystem watcher per scanned drive, plus the worker that drains their events, debounces them into
/// a set of changed directories and hands each batch to the host.
/// </summary>
internal sealed class FsWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);
    private const int MaxBatch = 10_000;
    private const int WatcherBufferSize = 64 * 1024;

    private readonly Dictionary<char, FileSystemWatcher> _watchers = [];
    private readonly Lock _watchersLock = new();
    /// <summary>Changed directories from the watchers, drained and debounced by the worker.</summary>
    private readonly BlockingCollection<string> _changes = new(new ConcurrentQueue<string>());
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<HashSet<string>> _processBatch;
    private readonly Func<bool> _mayProcess;
    private readonly Thread _worker;
    private int _suppress;

    /// <summary>Starts the debouncing worker.</summary>
    /// <param name="processBatch">Called on the worker thread with each debounced batch of changed directories.</param>
    /// <param name="mayProcess">Checked before each batch; a batch is dropped when it returns false (a deletion runs).</param>
    public FsWatcher(Action<HashSet<string>> processBatch, Func<bool> mayProcess)
    {
        _processBatch = processBatch;
        _mayProcess = mayProcess;
        _worker = new Thread(Work) { Name = "fs-watch", IsBackground = true };
        _worker.Start();
    }

    /// <summary>
    /// Set while a deletion runs so watcher events (including the deletion's own) are dropped at the source, avoiding
    /// both double-counting and a flood of events during a big delete.
    /// </summary>
    public bool Suppress
    {
        get => Volatile.Read(ref _suppress) != 0;
        set => Volatile.Write(ref _suppress, value ? 1 : 0);
    }

    /// <summary>
    /// Starts a recursive watcher on a drive's root (idempotent per drive). Each change is queued as the affected
    /// directory, unless a deletion is currently suppressing events.
    /// </summary>
    public void Watch(char letter)
    {
        lock (_watchersLock)
        {
            if (_stop.IsCancellationRequested || _watchers.ContainsKey(letter))
            {
                return;
            }
            var root = $"{letter}:\\";
            FileSystemWatcher watcher;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = WatcherBufferSize,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                };
                watcher.Created += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                // The watcher's buffer overflowed and events were lost: refresh the drive root, the one directory we
                // know is affected.
                watcher.Error += (_, _) => Enqueue(root);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException)
            {
                Trace.TraceWarning($"watch {root}: {e.Message}");
                return;
            }
            _watchers.Add(letter, watcher);
        }
    }

    /// <summary>Stops every watcher and the worker.</summary>
    public void Dispose()
    {
        lock (_watchersLock)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }
            _stop.Cancel();
            foreach (var w in _watchers.Values)
            {
                w.Dispose();
            }
            _watchers.Clear();
        }
        _worker.Join();
        _changes.Dispose();
        _stop.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => EnqueueParentOf(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        EnqueueParentOf(e.OldFullPath);
        EnqueueParentOf(e.FullPath);
    }

    private void EnqueueParentOf(string path)
    {
        if (Path.GetDirectoryName(path) is { } dir)
        {
            Enqueue(dir);
        }
    }

    private void Enqueue(string dir)
    {
        if (Suppress || _stop.IsCancellationRequested)
        {
            return;
        }
        try
        {
            _changes.Add(dir);
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose: the event no longer matters.
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Work()
    {
        var token = _stop.Token;
        try
        {
            while (true)
            {
                var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _changes.Take(token) };
                var deadline = Stopwatch.StartNew();
                while (batch.Count < MaxBatch)
                {
                    var remaining = Debounce - deadline.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }
                    var wait = remaining < Quiet ? remaining : Quiet;
                    if (!_changes.TryTake(out var dir, (int)Math.Ceiling(wait.TotalMilliseconds), token))
                    {
                        break; // a quiet gap: process what we have
                    }
                    batch.Add(dir);
                }
                if (_mayProcess())
                {
                    try
                    {
                        _processBatch(batch);
                    }
                    catch (Exception e)
                    {
                        // A failed refresh must not kill the worker (an unhandled exception would end the process).
                        Trace.TraceError($"fs-watch: refresh failed: {e}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }
}
