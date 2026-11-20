// Job progress and banner texts (model.ts jobProgress / jobText / buildJobLines).

using DeleteMan.Engine;

namespace DeleteMan.ViewModels;

/// <param name="Fraction">0..1, or null for an indeterminate bar.</param>
/// <param name="Text">Progress text.</param>
public sealed record ProgressView(double? Fraction, string Text);

/// <summary>A row's own deletion progress.</summary>
public sealed record DeletingView(string JobId, double? Fraction, string Text);

public sealed record JobLineView(string Id, JobStatus Status, string Path, string Text, bool Resumed, bool Active, bool HasErrors);

public static class Jobs
{
    public static bool IsActive(JobView j) => j.Status is JobStatus.Running or JobStatus.Queued;

    public static bool IsBusy(IReadOnlyList<JobView> jobs) => jobs.Any(IsActive);

    public static ProgressView Progress(JobView j)
    {
        if (j.Status == JobStatus.Queued)
        {
            return new ProgressView(null, "Queued…");
        }
        if (j.Mode == DeleteMode.Recycle)
        {
            return new ProgressView(null, "Moving to Recycle Bin…");
        }
        double? fraction = j.ExpectedBytes is { } eb && eb != 0 ? Math.Min(1, (double)j.BytesDeleted / eb) : null;
        var freed = j.ExpectedBytes is { } expected
            ? $"{Format.Bytes(j.BytesDeleted)} / {(j.ExpectedComplete ? "" : "≥")}{Format.Bytes(expected)} freed"
            : $"{Format.Bytes(j.BytesDeleted)} freed";
        string[] parts =
        [
            fraction is { } f ? $"Deleting… {Format.Percent(f)}" : "Deleting…",
            Format.Plural(j.FilesDeleted, "file"),
            freed,
            Format.Duration(j.ElapsedMs),
        ];
        return new ProgressView(fraction, string.Join(" · ", parts));
    }

    public static string Text(JobView j) => j.Status switch
    {
        JobStatus.Queued or JobStatus.Running => Progress(j).Text,
        JobStatus.Done => j.Mode == DeleteMode.Recycle
            ? $"Moved to Recycle Bin · {Format.Duration(j.ElapsedMs)}"
            : $"Deleted {Format.Plural(j.FilesDeleted, "file")} · {Format.Bytes(j.BytesDeleted)} freed · {Format.Duration(j.ElapsedMs)}",
        JobStatus.DoneWithErrors =>
            $"Finished with {Format.Plural(j.ErrorCount, "error")} · {Format.Plural(j.FilesDeleted, "file")} deleted · {Format.Bytes(j.BytesDeleted)} freed",
        JobStatus.Cancelled => $"Cancelled · {Format.Plural(j.FilesDeleted, "file")} deleted ({Format.Bytes(j.BytesDeleted)})",
        _ => $"Failed: {j.Message ?? "unknown error"}",
    };

    public static IReadOnlyList<JobLineView> BuildLines(IReadOnlyList<JobView> jobs) =>
        jobs.Select(j => new JobLineView(
                j.Id,
                j.Status,
                j.Path,
                Text(j),
                j.Resumed,
                IsActive(j),
                j.Status is JobStatus.DoneWithErrors or JobStatus.Failed))
            .ToList();

    /// <summary>Active job deleting exactly <paramref name="path"/> (case-insensitive), if any.</summary>
    internal static JobView? ActiveJobFor(IReadOnlyList<JobView> jobs, string path) =>
        jobs.FirstOrDefault(j => IsActive(j) && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));

    internal static ProgressView? SegProgress(IReadOnlyList<JobView> jobs, string path) =>
        ActiveJobFor(jobs, path) is { } j ? Progress(j) : null;
}
