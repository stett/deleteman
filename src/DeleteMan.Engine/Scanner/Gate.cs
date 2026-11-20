namespace DeleteMan.Engine;

/// <summary>Lets a deletion job stop all scanning while it runs, and wait until no directory listing is in flight.</summary>
public sealed class Gate
{
    private int _paused;
    private int _inflight;

    /// <summary>True while paused.</summary>
    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    /// <summary>Blocks while paused, then registers one in-flight listing until the guard is disposed.</summary>
    public GateGuard Enter()
    {
        while (true)
        {
            if (!IsPaused)
            {
                Interlocked.Increment(ref _inflight);
                if (!IsPaused)
                {
                    return new GateGuard(this);
                }
                Interlocked.Decrement(ref _inflight);
            }
            Thread.Sleep(20);
        }
    }

    /// <summary>Pauses new listings and waits for the in-flight ones to finish.</summary>
    public void PauseAndDrain()
    {
        Volatile.Write(ref _paused, 1);
        while (Volatile.Read(ref _inflight) > 0)
        {
            Thread.Sleep(5);
        }
    }

    /// <summary>Lets listings run again.</summary>
    public void Resume() => Volatile.Write(ref _paused, 0);

    internal void Leave() => Interlocked.Decrement(ref _inflight);
}

/// <summary>One in-flight listing registered with a <see cref="Gate"/>; dispose it when the listing is done.</summary>
public readonly struct GateGuard : IDisposable
{
    private readonly Gate? _gate;

    internal GateGuard(Gate gate) => _gate = gate;

    /// <summary>Unregisters the listing.</summary>
    public void Dispose() => _gate?.Leave();
}
