// Command-line driver for the deletion engine, used by scripts\bench.ps1 (port of the Rust bench_delete.rs).
//
// Usage: DeleteMan.Bench <directory> [threads]
//        DeleteMan.Bench --recycle <directory>
// Deletes the directory tree permanently (no confirmation!) and prints timing statistics. With --recycle the tree is
// moved to the Recycle Bin through the same engine path the app uses (handy for checking the shell COM path in a
// Native AOT build).
// Exit codes: 0 done, 1 finished with errors / cancelled / failed, 2 bad arguments.

using System.Diagnostics;
using System.Globalization;
using DeleteMan.Engine;

var recycle = args.Length > 0 && args[0] == "--recycle";
if (recycle)
{
    args = args[1..];
}
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: DeleteMan.Bench <directory> [threads]");
    Console.Error.WriteLine("       DeleteMan.Bench --recycle <directory>");
    return 2;
}

var threads = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
    ? parsed
    : DeletePool.DefaultThreadCount();
var path = WidePath.FromDisplay(args[0]);
if (path.IsVolumeRoot)
{
    Console.Error.WriteLine("refusing to delete a volume root");
    return 2;
}

using var pool = new DeletePool("delete", threads);
var mode = recycle ? DeleteMode.Recycle : DeleteMode.Permanent;
var job = new Job(1, path, mode, isDir: true, node: null, expectedBytes: null, expectedFiles: null, expectedComplete: true);
var watch = Stopwatch.StartNew();
Deleter.Run(job, pool);
var seconds = watch.Elapsed.TotalSeconds;

var files = job.FilesDeleted;
var dirs = job.DirsDeleted;
var perSecond = seconds > 0 ? (files + dirs) / seconds : 0;
Console.WriteLine($"status:  {job.Status}");
Console.WriteLine($"threads: {threads}");
Console.WriteLine($"files:   {files}");
Console.WriteLine($"dirs:    {dirs}");
Console.WriteLine($"bytes:   {job.BytesDeleted}");
Console.WriteLine($"errors:  {job.ErrorCount}");
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seconds: {seconds:F3}"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"files/s: {perSecond:F0}"));
foreach (var error in job.Errors.Take(20))
{
    Console.Error.WriteLine($"  {error.Path} — {error.Message} ({error.Code})");
}
return job.Status == JobStatus.Done ? 0 : 1;
