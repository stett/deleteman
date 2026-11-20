using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace DeleteMan.App.Shell;

/// <summary>
/// Opt-in timing diagnostics (set <c>DELETEMAN_PERF</c> to <c>1</c> for the log, or to a file path): UI-thread stalls (frame gaps), the time each poll
/// phase takes, and GC pressure, all written to the log. Off, every call here is a cheap flag check.
/// </summary>
internal static class Perf
{
    /// <summary>True when the environment asks for timings.</summary>
    public static readonly bool Enabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DELETEMAN_PERF"));

    private static long _lastFrame;
    private static long _lastReport;
    private static int _frames;
    private static int _slowFrames;
    private static double _worstGap;
    private static string _lastPhase = "start";
    private static int _gen0, _gen1, _gen2;
    private static long _allocated;
    private static TimeSpan _pause;
    private static StreamWriter? _file;

    /// <summary>Starts the frame-gap and GC monitors (UI thread).</summary>
    public static void Start()
    {
        if (!Enabled)
        {
            return;
        }
        // DELETEMAN_PERF=1 writes into the app log; any other value is a file to write to (the log is shared by
        // every instance and rewritten on each start, so a measurement run wants its own file).
        string target = Environment.GetEnvironmentVariable("DELETEMAN_PERF") ?? "";
        if (target != "1")
        {
            try
            {
                _file = new StreamWriter(target, append: false) { AutoFlush = true };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Trace.TraceError($"perf: cannot write {target}: {e.Message}");
            }
        }
        _lastFrame = _lastReport = Stopwatch.GetTimestamp();
        _gen0 = GC.CollectionCount(0);
        _gen1 = GC.CollectionCount(1);
        _gen2 = GC.CollectionCount(2);
        _allocated = GC.GetTotalAllocatedBytes();
        _pause = GC.GetTotalPauseDuration();
        Write($"perf {DateTime.Now:HH:mm:ss.fff}: on ({Environment.ProcessorCount} logical processors, server GC {System.Runtime.GCSettings.IsServerGC}, latency {System.Runtime.GCSettings.LatencyMode})");
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Times a phase; logs it when it took 4 ms or more. The phase name also labels the next frame gap.</summary>
    public static Scope Phase(string name) => Enabled ? new Scope(name, Stopwatch.GetTimestamp()) : default;

    /// <summary>Writes one timing line (only when enabled).</summary>
    public static void Note(string text)
    {
        if (Enabled)
        {
            Write($"perf {DateTime.Now:HH:mm:ss.fff}: {text}");
        }
    }

    private static void Write(string line)
    {
        if (_file is not null)
        {
            _file.WriteLine(line);
        }
        else
        {
            Trace.TraceInformation(line);
        }
    }

    private static void OnRendering(object? sender, object e)
    {
        long now = Stopwatch.GetTimestamp();
        double gap = Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds;
        _lastFrame = now;
        _frames++;
        if (gap > 40)
        {
            _slowFrames++;
            _worstGap = Math.Max(_worstGap, gap);
            Write($"perf {DateTime.Now:HH:mm:ss.fff}: frame gap {gap:F0} ms after {_lastPhase}");
        }
        if (Stopwatch.GetElapsedTime(_lastReport, now).TotalSeconds >= 2)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long alloc = GC.GetTotalAllocatedBytes();
            TimeSpan pause = GC.GetTotalPauseDuration();
            double secs = Stopwatch.GetElapsedTime(_lastReport, now).TotalSeconds;
            Write(
                $"perf {DateTime.Now:HH:mm:ss.fff}: {secs:F1} s: {_frames / secs:F0} fps, {_slowFrames} frames > 40 ms (worst {_worstGap:F0} ms); "
                + $"GC gen0/1/2 {g0 - _gen0}/{g1 - _gen1}/{g2 - _gen2}, {(alloc - _allocated) / 1048576.0 / secs:F0} MB/s allocated, "
                + $"{(pause - _pause).TotalMilliseconds:F0} ms paused; heap {GC.GetTotalMemory(false) / 1048576} MB");
            _lastReport = now;
            _frames = _slowFrames = 0;
            _worstGap = 0;
            (_gen0, _gen1, _gen2, _allocated, _pause) = (g0, g1, g2, alloc, pause);
        }
    }

    /// <summary>A timed phase; dispose to record it.</summary>
    public readonly struct Scope(string name, long started) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose()
        {
            if (name is null)
            {
                return;
            }
            _lastPhase = name;
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms >= 4)
            {
                Write($"perf {DateTime.Now:HH:mm:ss.fff}: {name} {ms:F1} ms");
            }
        }
    }
}
