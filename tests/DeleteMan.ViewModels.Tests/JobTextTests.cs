using DeleteMan.Engine;
using DeleteMan.ViewModels.Tests.TestSupport;

namespace DeleteMan.ViewModels.Tests;

/// <summary>Job progress and banner texts (model.ts jobProgress / jobText / buildJobLines).</summary>
public class JobTextTests
{
    [Fact]
    public void Active_and_busy()
    {
        Assert.True(Jobs.IsActive(Make.Job(status: JobStatus.Queued)));
        Assert.True(Jobs.IsActive(Make.Job(status: JobStatus.Running)));
        Assert.False(Jobs.IsActive(Make.Job(status: JobStatus.Done)));
        Assert.False(Jobs.IsActive(Make.Job(status: JobStatus.DoneWithErrors)));
        Assert.False(Jobs.IsActive(Make.Job(status: JobStatus.Cancelled)));
        Assert.False(Jobs.IsActive(Make.Job(status: JobStatus.Failed)));
        Assert.True(Jobs.IsBusy([Make.Job(status: JobStatus.Done), Make.Job(status: JobStatus.Queued)]));
        Assert.False(Jobs.IsBusy([Make.Job(status: JobStatus.Done)]));
        Assert.False(Jobs.IsBusy([]));
    }

    [Fact]
    public void Progress_for_queued_and_recycle_is_indeterminate()
    {
        Assert.Equal(new ProgressView(null, "Queued…"), Jobs.Progress(Make.Job(status: JobStatus.Queued)));
        Assert.Equal(new ProgressView(null, "Moving to Recycle Bin…"), Jobs.Progress(Make.Job(mode: DeleteMode.Recycle)));
    }

    [Fact]
    public void Progress_for_a_running_permanent_job()
    {
        var p = Jobs.Progress(Make.Job());
        Assert.Equal(0.25, p.Fraction);
        Assert.Equal("Deleting… 25 % · 3 files · 250 B / 1000 B freed · 1:05", p.Text);

        var partial = Jobs.Progress(Make.Job(expectedComplete: false));
        Assert.Equal("Deleting… 25 % · 3 files · 250 B / ≥1000 B freed · 1:05", partial.Text);

        var unknown = Jobs.Progress(Make.Job(expectedBytes: null));
        Assert.Null(unknown.Fraction);
        Assert.Equal("Deleting… · 3 files · 250 B freed · 1:05", unknown.Text);

        var over = Jobs.Progress(Make.Job(bytes: 5000));
        Assert.Equal(1.0, over.Fraction);
        Assert.StartsWith("Deleting… 100 %", over.Text);

        var one = Jobs.Progress(Make.Job(files: 1));
        Assert.Contains("· 1 file ·", one.Text);
    }

    [Fact]
    public void Text_per_status()
    {
        Assert.Equal("Deleting… 25 % · 3 files · 250 B / 1000 B freed · 1:05", Jobs.Text(Make.Job()));
        Assert.Equal("Queued…", Jobs.Text(Make.Job(status: JobStatus.Queued)));
        Assert.Equal("Deleted 3 files · 250 B freed · 1:05", Jobs.Text(Make.Job(status: JobStatus.Done)));
        Assert.Equal("Moved to Recycle Bin · 1:05", Jobs.Text(Make.Job(status: JobStatus.Done, mode: DeleteMode.Recycle)));
        Assert.Equal("Finished with 2 errors · 3 files deleted · 250 B freed", Jobs.Text(Make.Job(status: JobStatus.DoneWithErrors, errorCount: 2)));
        Assert.Equal("Finished with 1 error · 3 files deleted · 250 B freed", Jobs.Text(Make.Job(status: JobStatus.DoneWithErrors, errorCount: 1)));
        Assert.Equal("Cancelled · 3 files deleted (250 B)", Jobs.Text(Make.Job(status: JobStatus.Cancelled)));
        Assert.Equal("Failed: boom", Jobs.Text(Make.Job(status: JobStatus.Failed, message: "boom")));
        Assert.Equal("Failed: unknown error", Jobs.Text(Make.Job(status: JobStatus.Failed)));
    }

    [Fact]
    public void Job_lines_carry_status_flags_in_order()
    {
        var lines = Jobs.BuildLines(
        [
            Make.Job("1", status: JobStatus.Running, resumed: true),
            Make.Job("2", path: @"D:\x", status: JobStatus.DoneWithErrors, errorCount: 1),
            Make.Job("3", status: JobStatus.Failed, message: "bad"),
            Make.Job("4", status: JobStatus.Done),
        ]);
        Assert.Equal(4, lines.Count);
        Assert.Equal(new JobLineView("1", JobStatus.Running, @"C:\Users", "Deleting… 25 % · 3 files · 250 B / 1000 B freed · 1:05", Resumed: true, Active: true, HasErrors: false), lines[0]);
        Assert.Equal(new JobLineView("2", JobStatus.DoneWithErrors, @"D:\x", "Finished with 1 error · 3 files deleted · 250 B freed", Resumed: false, Active: false, HasErrors: true), lines[1]);
        Assert.True(lines[2].HasErrors);
        Assert.False(lines[2].Active);
        Assert.False(lines[3].HasErrors);
    }
}
