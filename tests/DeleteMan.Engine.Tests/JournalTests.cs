using DeleteMan.Engine.Tests.TestSupport;

namespace DeleteMan.Engine.Tests;

/// <summary>
/// Port of the journal.rs tests plus the on-disk compatibility contract: the C# journal must read and write exactly
/// what the Rust build wrote (snake_case keys, lowercase/snake_case enum names, explicit nulls), so a deletion that
/// was running under the old build resumes under the new one.
/// </summary>
public class JournalTests
{
    private static JobRecord Rec(ulong id, JobStatus status) => new()
    {
        Id = id,
        Path = $@"D:\x\{id}",
        Mode = DeleteMode.Permanent,
        IsDir = true,
        ExpectedBytes = 10,
        ExpectedFiles = 1,
        ExpectedComplete = true,
        BytesDeleted = 0,
        FilesDeleted = 0,
        DirsDeleted = 0,
        ErrorCount = 0,
        Errors = [],
        Status = status,
        StartedMs = 1,
        UpdatedMs = 2,
        ElapsedMs = 3,
        Resumed = false,
        Message = null,
    };

    [Fact]
    public void Save_then_load_roundtrips()
    {
        using var tmp = new TempDir();
        var j = new Journal(tmp.Sub("data"));
        Assert.Empty(j.Load());
        j.Save([Rec(1, JobStatus.Running), Rec(2, JobStatus.Done)]);
        Assert.True(File.Exists(j.Path));
        Assert.EndsWith("jobs.json", j.Path);
        var back = j.Load();
        Assert.Equal(2, back.Count);
        Assert.Equal(JobStatus.Running, back[0].Status);
        Assert.Equal(@"D:\x\2", back[1].Path);
        Assert.Equal(10UL, back[1].ExpectedBytes);
        j.Save([]);
        Assert.Empty(j.Load());
    }

    [Fact]
    public void Corrupt_file_is_set_aside()
    {
        using var tmp = new TempDir();
        var j = new Journal(tmp.Path);
        File.WriteAllText(j.Path, "{ not json");
        Assert.Empty(j.Load());
        Assert.True(File.Exists(Path.Combine(tmp.Path, "jobs.json.corrupt")));
        Assert.False(File.Exists(j.Path));
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temp_file()
    {
        using var tmp = new TempDir();
        var j = new Journal(tmp.Path);
        j.Save([Rec(1, JobStatus.Done)]);
        j.Save([Rec(1, JobStatus.Done), Rec(2, JobStatus.Failed)]);
        Assert.Single(Directory.GetFiles(tmp.Path));
        Assert.Equal(2, j.Load().Count);
    }

    [Fact]
    public void Written_json_uses_the_rust_names_and_explicit_nulls()
    {
        using var tmp = new TempDir();
        var j = new Journal(tmp.Path);
        var rec = Rec(9, JobStatus.DoneWithErrors) with
        {
            Mode = DeleteMode.Recycle,
            ExpectedBytes = null,
            Errors = [new JobError(@"D:\x\9\a.bin", 32, "sharing violation")],
            Message = "boom",
        };
        j.Save([rec]);
        var text = File.ReadAllText(j.Path);
        Assert.Contains("\"version\": 1", text);
        Assert.Contains("\"jobs\":", text);
        Assert.Contains("\"status\": \"done_with_errors\"", text);
        Assert.Contains("\"mode\": \"recycle\"", text);
        Assert.Contains("\"expected_bytes\": null", text);
        Assert.Contains("\"expected_files\": 1", text);
        Assert.Contains("\"is_dir\": true", text);
        Assert.Contains("\"bytes_deleted\": 0", text);
        Assert.Contains("\"error_count\": 0", text);
        Assert.Contains("\"started_ms\": 1", text);
        Assert.Contains("\"elapsed_ms\": 3", text);
        Assert.Contains("\"resumed\": false", text);
        Assert.Contains("\"message\": \"boom\"", text);
        Assert.Contains("\"code\": 32", text);
        Assert.DoesNotContain("DoneWithErrors", text);
        Assert.DoesNotContain("expectedBytes", text);
    }

    /// <summary>Exactly what serde_json::to_string_pretty wrote in the Rust build.</summary>
    private const string RustJournal = """
        {
          "version": 1,
          "jobs": [
            {
              "id": 7,
              "path": "D:\\tmp\\tree",
              "mode": "permanent",
              "is_dir": true,
              "expected_bytes": 1234,
              "expected_files": 10,
              "expected_complete": true,
              "bytes_deleted": 100,
              "files_deleted": 2,
              "dirs_deleted": 1,
              "error_count": 0,
              "errors": [],
              "status": "running",
              "started_ms": 1700000000000,
              "updated_ms": 1700000001000,
              "elapsed_ms": 1000,
              "resumed": false,
              "message": null
            },
            {
              "id": 8,
              "path": "D:\\tmp\\x",
              "mode": "recycle",
              "is_dir": false,
              "expected_bytes": null,
              "expected_files": null,
              "expected_complete": false,
              "bytes_deleted": 0,
              "files_deleted": 0,
              "dirs_deleted": 0,
              "error_count": 1,
              "errors": [
                {
                  "path": "D:\\tmp\\x\\a.bin",
                  "code": 32,
                  "message": "The process cannot access the file because it is being used by another process. (os error 32)"
                }
              ],
              "status": "done_with_errors",
              "started_ms": 1,
              "updated_ms": 2,
              "elapsed_ms": 3,
              "resumed": true,
              "message": "Recycle Bin: aborted"
            }
          ]
        }
        """;

    [Fact]
    public void Journal_written_by_the_rust_build_loads()
    {
        using var tmp = new TempDir();
        var j = new Journal(tmp.Path);
        File.WriteAllText(j.Path, RustJournal);
        var records = j.Load();
        Assert.Equal(2, records.Count);

        var r0 = records[0];
        Assert.Equal(7UL, r0.Id);
        Assert.Equal(@"D:\tmp\tree", r0.Path);
        Assert.Equal(DeleteMode.Permanent, r0.Mode);
        Assert.True(r0.IsDir);
        Assert.Equal(1234UL, r0.ExpectedBytes);
        Assert.Equal(10UL, r0.ExpectedFiles);
        Assert.Equal(JobStatus.Running, r0.Status);
        Assert.Equal(100UL, r0.BytesDeleted);
        Assert.Equal(1700000000000UL, r0.StartedMs);
        Assert.Null(r0.Message);

        var r1 = records[1];
        Assert.Equal(DeleteMode.Recycle, r1.Mode);
        Assert.False(r1.IsDir);
        Assert.Null(r1.ExpectedBytes);
        Assert.Equal(JobStatus.DoneWithErrors, r1.Status);
        Assert.Single(r1.Errors);
        Assert.Equal(32U, r1.Errors[0].Code);
        Assert.True(r1.Resumed);
        Assert.Equal("Recycle Bin: aborted", r1.Message);
    }

    [Fact]
    public void Missing_optional_fields_default_like_serde()
    {
        // errors / resumed / message carry #[serde(default)] in the Rust struct; older files may omit them.
        using var tmp = new TempDir();
        var j = new Journal(tmp.Path);
        File.WriteAllText(j.Path, """
            {"version":1,"jobs":[{"id":1,"path":"D:\\a","mode":"permanent","is_dir":true,"expected_bytes":null,
            "expected_files":null,"expected_complete":true,"bytes_deleted":0,"files_deleted":0,"dirs_deleted":0,
            "error_count":0,"status":"queued","started_ms":1,"updated_ms":1,"elapsed_ms":0}]}
            """);
        var rec = Assert.Single(j.Load());
        Assert.Empty(rec.Errors);
        Assert.False(rec.Resumed);
        Assert.Null(rec.Message);
        Assert.Equal(JobStatus.Queued, rec.Status);
    }

    [Fact]
    public void Record_roundtrip_marks_active_jobs_resumed()
    {
        using var tmp = new TempDir();
        var j = new Journal(tmp.Path);
        var running = Rec(1, JobStatus.Running) with { Path = @"D:\nowhere\x", FilesDeleted = 10, ElapsedMs = 500 };
        j.Save([running]);
        var back = j.Load();
        var resumed = Job.FromRecord(back[0]);
        Assert.True(resumed.Resumed);
        Assert.Equal(JobStatus.Queued, resumed.Status);
        Assert.Equal(10UL, resumed.FilesDeleted);
        Assert.Equal(@"D:\nowhere\x", resumed.Path.Display);
        // Elapsed time accumulated by the earlier run carries over into the resumed job.
        Assert.True(resumed.ElapsedMs >= 500);

        var finished = Job.FromRecord(Rec(2, JobStatus.Done) with { ElapsedMs = 77 });
        Assert.False(finished.Resumed);
        Assert.Equal(JobStatus.Done, finished.Status);
        Assert.Equal(77UL, finished.ElapsedMs);
        Assert.Equal(JobStatus.Done, finished.ToRecord().Status);
    }

    [Fact]
    public void Fresh_job_record_matches_its_constructor_arguments()
    {
        var job = new Job(11, WidePath.FromDisplay(@"C:\Users\x\stuff"), DeleteMode.Permanent, isDir: true, node: new NodeRef('C', 5), expectedBytes: 99, expectedFiles: 3, expectedComplete: false);
        var rec = job.ToRecord();
        Assert.Equal(11UL, rec.Id);
        Assert.Equal(@"C:\Users\x\stuff", rec.Path);
        Assert.Equal(JobStatus.Queued, rec.Status);
        Assert.Equal(99UL, rec.ExpectedBytes);
        Assert.Equal(3UL, rec.ExpectedFiles);
        Assert.False(rec.ExpectedComplete);
        Assert.False(rec.Resumed);
        Assert.Empty(rec.Errors);
    }
}
