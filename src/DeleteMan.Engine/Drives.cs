using Windows.Win32;

namespace DeleteMan.Engine;

/// <summary>What kind of volume a drive letter maps to (<c>GetDriveTypeW</c>).</summary>
public enum DriveKind
{
    /// <summary>A local fixed disk.</summary>
    Fixed,

    /// <summary>Removable media (USB stick, card reader).</summary>
    Removable,

    /// <summary>A network share.</summary>
    Remote,

    /// <summary>An optical drive.</summary>
    Cdrom,

    /// <summary>A RAM disk.</summary>
    Ramdisk,

    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>One mounted volume.</summary>
/// <param name="Letter">Drive letter, e.g. <c>"C"</c>.</param>
/// <param name="Label">Volume label (may be empty).</param>
/// <param name="Fs">File system name, e.g. <c>"NTFS"</c>.</param>
/// <param name="Kind">Volume kind.</param>
/// <param name="Total">Capacity in bytes.</param>
/// <param name="Free">Bytes available to the current user.</param>
public sealed record DriveInfo(string Letter, string Label, string Fs, DriveKind Kind, ulong Total, ulong Free);

/// <summary>Volume enumeration.</summary>
public static unsafe class Drives
{
    // GetDriveTypeW results (winbase.h).
    private const uint DriveNoRootDir = 1;
    private const uint DriveRemovable = 2;
    private const uint DriveFixed = 3;
    private const uint DriveRemote = 4;
    private const uint DriveCdrom = 5;
    private const uint DriveRamdisk = 6;

    /// <summary>
    /// Enumerates every drive letter that has a mounted volume with a non-zero capacity. May block on unreachable
    /// network volumes; prefer <see cref="DriveCache"/> from UI-facing code.
    /// </summary>
    public static IReadOnlyList<DriveInfo> Enumerate()
    {
        var mask = PInvoke.GetLogicalDrives();
        var drives = new List<DriveInfo>();
        for (var i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) != 0 && Probe((char)('A' + i)) is { } info)
            {
                drives.Add(info);
            }
        }
        return drives;
    }

    private static DriveInfo? Probe(char letter)
    {
        var root = $"{letter}:\\";
        fixed (char* r = root)
        {
            var kind = PInvoke.GetDriveType(r) switch
            {
                DriveNoRootDir => (DriveKind?)null,
                DriveFixed => DriveKind.Fixed,
                DriveRemovable => DriveKind.Removable,
                DriveRemote => DriveKind.Remote,
                DriveCdrom => DriveKind.Cdrom,
                DriveRamdisk => DriveKind.Ramdisk,
                _ => DriveKind.Unknown,
            };
            if (kind is null)
            {
                return null;
            }
            ulong avail;
            ulong total;
            ulong freeTotal;
            if (!PInvoke.GetDiskFreeSpaceEx(r, &avail, &total, &freeTotal) || total == 0)
            {
                return null;
            }
            const int BufLen = 261;
            var label = stackalloc char[BufLen];
            var fs = stackalloc char[BufLen];
            label[0] = '\0';
            fs[0] = '\0';
            _ = PInvoke.GetVolumeInformation(r, label, BufLen, null, null, null, fs, BufLen);
            return new DriveInfo(letter.ToString(), new string(label), new string(fs), kind.Value, total, avail);
        }
    }
}

/// <summary>
/// Snapshot of <see cref="Drives.Enumerate"/> refreshed on a background thread, so the UI's frequent polls never
/// block on a slow network or virtual drive.
/// </summary>
public sealed class DriveCache : IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _worker;
    private volatile IReadOnlyList<DriveInfo> _snapshot;
    private int _dirty;

    private DriveCache()
    {
        _snapshot = Drives.Enumerate();
        _worker = new Thread(Run) { Name = "drive-cache", IsBackground = true };
    }

    /// <summary>Takes a first snapshot synchronously, then starts the background refresher.</summary>
    public static DriveCache Start()
    {
        var cache = new DriveCache();
        cache._worker.Start();
        return cache;
    }

    /// <summary>The latest snapshot.</summary>
    public IReadOnlyList<DriveInfo> Get() => _snapshot;

    /// <summary>Requests a refresh on the next tick (used after deletions change free space).</summary>
    public void Invalidate() => Volatile.Write(ref _dirty, 1);

    /// <summary>Stops the background thread.</summary>
    public void Dispose()
    {
        if (_stop.IsSet)
        {
            return;
        }
        _stop.Set();
        // The worker may be inside a slow enumeration (unreachable network drive); don't hang shutdown on it.
        _worker.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        var last = System.Diagnostics.Stopwatch.StartNew();
        while (!_stop.Wait(Tick))
        {
            if (Interlocked.Exchange(ref _dirty, 0) != 0 || last.Elapsed >= RefreshEvery)
            {
                _snapshot = Drives.Enumerate();
                last.Restart();
            }
        }
    }
}
