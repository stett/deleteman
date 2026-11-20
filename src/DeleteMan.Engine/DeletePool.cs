using System.Collections.Concurrent;

namespace DeleteMan.Engine;

/// <summary>
/// The thread pool deletions fan out onto: a fixed set of dedicated threads with 8 MB stacks (the deleter recurses
/// once per directory level), exposed as a <see cref="TaskScheduler"/> for <c>Parallel.ForEach</c>.
/// </summary>
public sealed class DeletePool : IDisposable
{
    /// <summary>Stack size of the deletion threads: deep trees recurse once per level.</summary>
    private const int StackSize = 8 << 20;

    private readonly DedicatedThreadPool _pool;

    /// <summary>Starts <paramref name="threads"/> threads named <c>{name}-{i}</c>.</summary>
    public DeletePool(string name, int threads) => _pool = new DedicatedThreadPool(name, threads, StackSize);

    /// <summary>Number of worker threads.</summary>
    public int Threads => _pool.MaximumConcurrencyLevel;

    /// <summary>The scheduler that runs work on the pool's threads.</summary>
    public TaskScheduler Scheduler => _pool;

    /// <summary>I/O-bound work benefits from more threads than cores, within reason: 2 × cores, clamped to 8..32.</summary>
    public static int DefaultThreadCount() => Math.Clamp(Environment.ProcessorCount * 2, 8, 32);

    /// <summary>Stops the threads once the queued work is done.</summary>
    public void Dispose() => _pool.Dispose();

    /// <summary>Runs <paramref name="action"/> on a pool thread and blocks until it finishes (rayon's <c>install</c>).</summary>
    internal void Install(Action action) => _pool.Install(action);
}

/// <summary>
/// A <see cref="TaskScheduler"/> backed by its own threads, so work queued here never competes with (or starves) the
/// .NET thread pool, and two pools never share threads.
/// </summary>
/// <remarks>
/// Pool threads may run a queued task inline when they wait for it. That is what makes nested
/// <c>Parallel.ForEach</c> calls behave like rayon's nested parallel iterators: a thread that waits for a replica
/// nobody has started yet runs it itself instead of blocking, so recursion can never exhaust the pool and deadlock.
/// Other threads never inline, so all work runs on the pool's (large-stack) threads.
/// </remarks>
internal sealed class DedicatedThreadPool : TaskScheduler, IDisposable
{
    [ThreadStatic]
    private static DedicatedThreadPool? t_owner;

    private readonly BlockingCollection<Task> _queue = new(new ConcurrentQueue<Task>());
    private readonly Thread[] _threads;

    /// <summary>Starts <paramref name="threads"/> background threads named <c>{name}-{i}</c>.</summary>
    public DedicatedThreadPool(string name, int threads, int stackSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        _threads = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            _threads[i] = new Thread(Work, stackSize) { Name = $"{name}-{i}", IsBackground = true };
            _threads[i].Start();
        }
    }

    /// <inheritdoc/>
    public override int MaximumConcurrencyLevel => _threads.Length;

    /// <summary>Runs <paramref name="action"/> on a pool thread and blocks until it finishes, rethrowing its exception.</summary>
    public void Install(Action action)
    {
        var task = new Task(action);
        task.Start(this);
        // Wait on the handle, not Task.Wait: Task.Wait could inline the task on this (small-stack) thread.
        ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
        task.GetAwaiter().GetResult();
    }

    /// <summary>Stops accepting work and waits for the threads to drain the queue and exit.</summary>
    public void Dispose()
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }
        _queue.CompleteAdding();
        foreach (var t in _threads)
        {
            if (t != Thread.CurrentThread)
            {
                t.Join();
            }
        }
        _queue.Dispose();
    }

    /// <inheritdoc/>
    protected override void QueueTask(Task task) => _queue.Add(task);

    /// <inheritdoc/>
    /// <remarks>
    /// A task inlined here may still sit in the queue; when a worker dequeues it later, <c>TryExecuteTask</c> simply
    /// returns false because the task already ran.
    /// </remarks>
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        t_owner == this && TryExecuteTask(task);

    /// <inheritdoc/>
    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();

    private void Work()
    {
        t_owner = this;
        foreach (var task in _queue.GetConsumingEnumerable())
        {
            TryExecuteTask(task);
        }
    }
}
