using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace DeleteMan.Engine;

/// <summary>How a job removes its target.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeleteMode>))]
public enum DeleteMode
{
    /// <summary>Parallel NT-level deletion; nothing goes to the Recycle Bin.</summary>
    [JsonStringEnumMemberName("permanent")]
    Permanent,

    /// <summary>Moves the target to the Recycle Bin through the shell.</summary>
    [JsonStringEnumMemberName("recycle")]
    Recycle,
}

/// <summary>Lifecycle of a deletion job. The journal stores these as snake_case strings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus
{
    /// <summary>Created (or resumed from the journal) and not started yet.</summary>
    [JsonStringEnumMemberName("queued")]
    Queued,

    /// <summary>Deleting.</summary>
    [JsonStringEnumMemberName("running")]
    Running,

    /// <summary>Finished; everything is gone.</summary>
    [JsonStringEnumMemberName("done")]
    Done,

    /// <summary>Finished, but some entries could not be deleted (see the job's errors).</summary>
    [JsonStringEnumMemberName("done_with_errors")]
    DoneWithErrors,

    /// <summary>Stopped by the user; what was deleted stays deleted.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    /// <summary>The job could not run at all (see the job's message).</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}

/// <summary>Helpers for <see cref="JobStatus"/>.</summary>
public static class JobStatusExtensions
{
    /// <summary>True while the job is queued or running.</summary>
    public static bool IsActive(this JobStatus status) => status is JobStatus.Queued or JobStatus.Running;
}

/// <summary>One entry that could not be deleted: its path, the Win32 error code and the system message.</summary>
public sealed record JobError(string Path, uint Code, string Message);

/// <summary>A scan node reference: drive letter and arena index. Formats as <c>"D:7"</c>.</summary>
public readonly record struct NodeRef(char Letter, uint Index)
{
    /// <summary>The node id string the UI uses, e.g. <c>"D:7"</c>.</summary>
    public override string ToString() => $"{Letter}:{Index}";
}

/// <summary>Serialised form of a job stored in the journal (<c>jobs.json</c>).</summary>
/// <remarks>
/// Every member except <see cref="Errors"/>, <see cref="Resumed"/> and <see cref="Message"/> is required, like the
/// Rust struct (those three carried <c>#[serde(default)]</c>), so a journal missing a required key is treated as
/// corrupt rather than silently resumed with zeroes.
/// </remarks>
public sealed record JobRecord
{
    /// <summary>Job id.</summary>
    public required ulong Id { get; init; }

    /// <summary>Target path in display form (no <c>\\?\</c> prefix).</summary>
    public required string Path { get; init; }

    /// <summary>Permanent or Recycle Bin.</summary>
    public required DeleteMode Mode { get; init; }

    /// <summary>True for a directory target, false for a single file.</summary>
    public required bool IsDir { get; init; }

    /// <summary>Bytes the scan saw under the target when the job started, if known.</summary>
    public required ulong? ExpectedBytes { get; init; }

    /// <summary>Files the scan saw under the target when the job started, if known.</summary>
    public required ulong? ExpectedFiles { get; init; }

    /// <summary>True if the expected numbers come from a finished scan (not a live lower bound).</summary>
    public required bool ExpectedComplete { get; init; }

    /// <summary>Bytes deleted so far.</summary>
    public required ulong BytesDeleted { get; init; }

    /// <summary>Files deleted so far.</summary>
    public required ulong FilesDeleted { get; init; }

    /// <summary>Directories (and directory links) deleted so far.</summary>
    public required ulong DirsDeleted { get; init; }

    /// <summary>Number of errors (may exceed the length of <see cref="Errors"/>, which is capped).</summary>
    public required ulong ErrorCount { get; init; }

    /// <summary>The first errors, capped at <see cref="Job.MaxErrors"/>. Never null: a missing or null value reads as empty.</summary>
    /// <remarks>
    /// The source-generated deserializer sets every init-only property in one object initializer, passing null for a
    /// key the file omits, so the default has to live in the setter rather than in a property initializer.
    /// </remarks>
    public IReadOnlyList<JobError> Errors
    {
        get;
        init => field = value ?? [];
    } = [];

    /// <summary>Job status when the record was written.</summary>
    public required JobStatus Status { get; init; }

    /// <summary>Creation time, Unix milliseconds.</summary>
    public required ulong StartedMs { get; init; }

    /// <summary>Last progress time, Unix milliseconds.</summary>
    public required ulong UpdatedMs { get; init; }

    /// <summary>Time spent so far, across restarts.</summary>
    public required ulong ElapsedMs { get; init; }

    /// <summary>True if the job was restarted from the journal.</summary>
    public bool Resumed { get; init; }

    /// <summary>Failure message, if any.</summary>
    public string? Message { get; init; }
}

/// <summary>What the UI sees of a job.</summary>
/// <param name="Id">Job id as a string.</param>
/// <param name="Path">Target path (display form).</param>
/// <param name="Mode">Permanent or Recycle Bin.</param>
/// <param name="Status">Current status.</param>
/// <param name="NodeId">Scan node the job was started from (<c>"D:7"</c>), if any.</param>
/// <param name="ExpectedBytes">Bytes the scan saw under the target, if known.</param>
/// <param name="ExpectedFiles">Files the scan saw under the target, if known.</param>
/// <param name="ExpectedComplete">True if the expected numbers come from a finished scan.</param>
/// <param name="BytesDeleted">Bytes deleted so far.</param>
/// <param name="FilesDeleted">Files deleted so far.</param>
/// <param name="DirsDeleted">Directories deleted so far.</param>
/// <param name="ErrorCount">Number of errors.</param>
/// <param name="Errors">The first errors (capped).</param>
/// <param name="StartedMs">Creation time, Unix milliseconds.</param>
/// <param name="UpdatedMs">Last progress time, Unix milliseconds.</param>
/// <param name="ElapsedMs">Time spent so far, across restarts.</param>
/// <param name="Resumed">True if the job was restarted from the journal.</param>
/// <param name="Message">Failure message, if any.</param>
public sealed record JobView(
    string Id,
    string Path,
    DeleteMode Mode,
    JobStatus Status,
    string? NodeId,
    ulong? ExpectedBytes,
    ulong? ExpectedFiles,
    bool ExpectedComplete,
    ulong BytesDeleted,
    ulong FilesDeleted,
    ulong DirsDeleted,
    ulong ErrorCount,
    IReadOnlyList<JobError> Errors,
    ulong StartedMs,
    ulong UpdatedMs,
    ulong ElapsedMs,
    bool Resumed,
    string? Message);

/// <summary>
/// One deletion job. Shared between the worker threads, the host and the journal: counters are updated with
/// interlocked operations, errors and the message under a lock.
/// </summary>
public sealed class Job
{
    /// <summary>At most this many errors are kept (all are counted).</summary>
    public const int MaxErrors = 200;

    private readonly List<JobError> _errors;
    private readonly Lock _errorsLock = new();
    private readonly Lock _messageLock = new();
    /// <summary>Elapsed time accumulated by earlier runs of this job (before a restart).</summary>
    private readonly ulong _baseElapsedMs;
    private readonly Stopwatch _runStarted = Stopwatch.StartNew();
    private long _bytesDeleted;
    private long _filesDeleted;
    private long _dirsDeleted;
    private long _errorCount;
    private int _status;
    private int _cancel;
    private long _updatedMs;
    private long _finalElapsedMs;
    private string? _message;

    /// <summary>Creates a fresh, queued job.</summary>
    /// <param name="id">Job id.</param>
    /// <param name="path">Target path.</param>
    /// <param name="mode">Permanent or Recycle Bin.</param>
    /// <param name="isDir">True for a directory target, false for a single file.</param>
    /// <param name="node">Scan node the job was started from, if any.</param>
    /// <param name="expectedBytes">Bytes the scan saw under the target, if known.</param>
    /// <param name="expectedFiles">Files the scan saw under the target, if known.</param>
    /// <param name="expectedComplete">True if the expected numbers come from a finished scan.</param>
    public Job(ulong id, WidePath path, DeleteMode mode, bool isDir, NodeRef? node, ulong? expectedBytes, ulong? expectedFiles, bool expectedComplete)
    {
        var now = NowMs();
        Id = id;
        Path = path;
        Mode = mode;
        IsDir = isDir;
        Node = node;
        ExpectedBytes = expectedBytes;
        ExpectedFiles = expectedFiles;
        ExpectedComplete = expectedComplete;
        _errors = [];
        _status = (int)JobStatus.Queued;
        StartedMs = now;
        _updatedMs = (long)now;
    }

    private Job(JobRecord r, bool resumed)
    {
        Id = r.Id;
        Path = WidePath.FromDisplay(r.Path);
        Mode = r.Mode;
        IsDir = r.IsDir;
        ExpectedBytes = r.ExpectedBytes;
        ExpectedFiles = r.ExpectedFiles;
        ExpectedComplete = r.ExpectedComplete;
        _bytesDeleted = (long)r.BytesDeleted;
        _filesDeleted = (long)r.FilesDeleted;
        _dirsDeleted = (long)r.DirsDeleted;
        _errorCount = (long)r.ErrorCount;
        _errors = [.. r.Errors];
        _status = (int)(resumed ? JobStatus.Queued : r.Status);
        StartedMs = r.StartedMs;
        _updatedMs = (long)r.UpdatedMs;
        _baseElapsedMs = r.ElapsedMs;
        _finalElapsedMs = resumed ? 0 : (long)r.ElapsedMs;
        Resumed = resumed || r.Resumed;
        _message = r.Message;
    }

    /// <summary>Job id.</summary>
    public ulong Id { get; }

    /// <summary>Target path.</summary>
    public WidePath Path { get; }

    /// <summary>Permanent or Recycle Bin.</summary>
    public DeleteMode Mode { get; }

    /// <summary>True for a directory target, false for a single file.</summary>
    public bool IsDir { get; }

    /// <summary>Scan node the job was started from (drive letter, arena index), if any.</summary>
    public NodeRef? Node { get; }

    /// <summary>Bytes the scan saw under the target when the job started, if known.</summary>
    public ulong? ExpectedBytes { get; }

    /// <summary>Files the scan saw under the target when the job started, if known.</summary>
    public ulong? ExpectedFiles { get; }

    /// <summary>True if the expected numbers come from a finished scan.</summary>
    public bool ExpectedComplete { get; }

    /// <summary>Bytes deleted so far.</summary>
    public ulong BytesDeleted => (ulong)Interlocked.Read(ref _bytesDeleted);

    /// <summary>Files deleted so far.</summary>
    public ulong FilesDeleted => (ulong)Interlocked.Read(ref _filesDeleted);

    /// <summary>Directories (and directory links) deleted so far.</summary>
    public ulong DirsDeleted => (ulong)Interlocked.Read(ref _dirsDeleted);

    /// <summary>Number of errors (all of them, even beyond <see cref="MaxErrors"/>).</summary>
    public ulong ErrorCount => (ulong)Interlocked.Read(ref _errorCount);

    /// <summary>Snapshot of the first <see cref="MaxErrors"/> errors.</summary>
    public IReadOnlyList<JobError> Errors
    {
        get
        {
            lock (_errorsLock)
            {
                return [.. _errors];
            }
        }
    }

    /// <summary>Current status.</summary>
    public JobStatus Status => (JobStatus)Volatile.Read(ref _status);

    /// <summary>True once <see cref="RequestCancel"/> was called.</summary>
    public bool IsCancelRequested => Volatile.Read(ref _cancel) != 0;

    /// <summary>Creation time, Unix milliseconds.</summary>
    public ulong StartedMs { get; }

    /// <summary>Last progress time, Unix milliseconds.</summary>
    public ulong UpdatedMs => (ulong)Interlocked.Read(ref _updatedMs);

    /// <summary>Time spent so far across restarts: live while active, frozen once finished.</summary>
    public ulong ElapsedMs =>
        Status.IsActive() ? _baseElapsedMs + (ulong)_runStarted.ElapsedMilliseconds : (ulong)Interlocked.Read(ref _finalElapsedMs);

    /// <summary>True if the job was restarted from the journal.</summary>
    public bool Resumed { get; }

    /// <summary>Failure message, if any.</summary>
    public string? Message
    {
        get
        {
            lock (_messageLock)
            {
                return _message;
            }
        }
        internal set
        {
            lock (_messageLock)
            {
                _message = value;
            }
        }
    }

    /// <summary>Rebuilds a job from the journal. Active records come back as <see cref="JobStatus.Queued"/> and flagged resumed.</summary>
    public static Job FromRecord(JobRecord record) => new(record, record.Status.IsActive());

    /// <summary>Asks the job to stop. A Recycle Bin operation ignores it (it cannot be interrupted).</summary>
    public void RequestCancel() => Volatile.Write(ref _cancel, 1);

    /// <summary>The journal form of the job.</summary>
    public JobRecord ToRecord() => new()
    {
        Id = Id,
        Path = Path.Display,
        Mode = Mode,
        IsDir = IsDir,
        ExpectedBytes = ExpectedBytes,
        ExpectedFiles = ExpectedFiles,
        ExpectedComplete = ExpectedComplete,
        BytesDeleted = BytesDeleted,
        FilesDeleted = FilesDeleted,
        DirsDeleted = DirsDeleted,
        ErrorCount = ErrorCount,
        Errors = Errors,
        Status = Status,
        StartedMs = StartedMs,
        UpdatedMs = UpdatedMs,
        ElapsedMs = ElapsedMs,
        Resumed = Resumed,
        Message = Message,
    };

    /// <summary>The UI form of the job.</summary>
    public JobView ToView() => new(
        Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Path.Display,
        Mode,
        Status,
        Node?.ToString(),
        ExpectedBytes,
        ExpectedFiles,
        ExpectedComplete,
        BytesDeleted,
        FilesDeleted,
        DirsDeleted,
        ErrorCount,
        Errors,
        StartedMs,
        UpdatedMs,
        ElapsedMs,
        Resumed,
        Message);

    /// <summary>Current time as Unix milliseconds.</summary>
    internal static ulong NowMs() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal void SetStatus(JobStatus status)
    {
        Volatile.Write(ref _status, (int)status);
        Touch();
    }

    /// <summary>Records progress time.</summary>
    internal void Touch() => Interlocked.Exchange(ref _updatedMs, (long)NowMs());

    /// <summary>Freezes the elapsed time and publishes the final status.</summary>
    internal void Finish(JobStatus status)
    {
        Interlocked.Exchange(ref _finalElapsedMs, (long)(_baseElapsedMs + (ulong)_runStarted.ElapsedMilliseconds));
        SetStatus(status);
    }

    internal void AddBytes(ulong n) => Interlocked.Add(ref _bytesDeleted, (long)n);

    internal void AddFiles(ulong n) => Interlocked.Add(ref _filesDeleted, (long)n);

    internal void AddDirs(ulong n) => Interlocked.Add(ref _dirsDeleted, (long)n);

    /// <summary>Counts an error and keeps it if fewer than <see cref="MaxErrors"/> are stored.</summary>
    internal void RecordError(string path, int win32Code)
    {
        Interlocked.Increment(ref _errorCount);
        var message = new Win32Exception(win32Code).Message;
        lock (_errorsLock)
        {
            if (_errors.Count < MaxErrors)
            {
                _errors.Add(new JobError(path, (uint)win32Code, message));
            }
        }
    }
}
