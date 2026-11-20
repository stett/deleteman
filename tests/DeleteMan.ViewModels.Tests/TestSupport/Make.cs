using DeleteMan.Engine;
// Implicit usings bring System.IO.DriveInfo into scope; the engine's record is the one every test means.
using DriveInfo = DeleteMan.Engine.DriveInfo;

namespace DeleteMan.ViewModels.Tests.TestSupport;

/// <summary>Factories for the engine's view records with sensible defaults, so tests read as scenarios.</summary>
public static class Make
{
    public static DriveInfo Drive(string letter = "C", string label = "System", string fs = "NTFS", DriveKind kind = DriveKind.Fixed, ulong total = 1000, ulong free = 400) =>
        new(letter, label, fs, kind, total, free);

    public static ChildView Child(string id, string name, ulong bytes, ulong files, NodeState state = NodeState.Done, uint? errorCode = null) =>
        new(id, name, bytes, files, state, errorCode);

    public static NodeView Node(
        string id,
        string name,
        string path,
        bool isRoot = false,
        ulong total = 0,
        ulong files = 0,
        ulong dirs = 0,
        ulong own = 0,
        ulong ownFiles = 0,
        IReadOnlyList<ChildView>? children = null,
        NodeState state = NodeState.Done,
        uint? errorCode = null,
        ulong otherCount = 0,
        ulong otherBytes = 0,
        string? deletingJobId = null) =>
        new(id, name, path, isRoot, total, files, dirs, own, ownFiles, state, errorCode, children ?? [], otherCount, otherBytes, deletingJobId);

    public static ScanStatus Scan(string drive = "C", ScanState status = ScanState.Scanning, uint depth = 3, ulong dirs = 1234, ulong accounted = 500, ulong elapsed = 0) =>
        new(drive, status, depth, dirs, accounted, elapsed);

    public static JobView Job(
        string id = "1",
        string path = @"C:\Users",
        JobStatus status = JobStatus.Running,
        DeleteMode mode = DeleteMode.Permanent,
        ulong? expectedBytes = 1000,
        ulong? expectedFiles = 10,
        bool expectedComplete = true,
        ulong bytes = 250,
        ulong files = 3,
        ulong dirs = 1,
        ulong errorCount = 0,
        IReadOnlyList<JobError>? errors = null,
        ulong elapsed = 65_000,
        bool resumed = false,
        string? message = null,
        string? nodeId = null) =>
        new(id, path, mode, status, nodeId, expectedBytes, expectedFiles, expectedComplete, bytes, files, dirs, errorCount, errors ?? [], 1, 2, elapsed, resumed, message);

    public static FileView File(string name, ulong size, uint attrs = 0x20) => new(name, size, attrs);

    /// <summary>
    /// The standard fixture: drive C (1000 total, 400 free), a root node with four children (one folded, one
    /// access-denied), loose files in the root, and an in-progress scan that has accounted 500 of the 600 used bytes.
    /// </summary>
    public static Model Fixture(ScanState scanState = ScanState.Scanning)
    {
        var m = new Model();
        m.Drives = [Drive()];
        m.Nodes["C:0"] = Node("C:0", @"C:\", @"C:\", isRoot: true, total: 500, files: 18, dirs: 4, own: 50, ownFiles: 2,
            children:
            [
                Child("C:1", "Users", 300, 10),
                Child("C:2", "Windows", 140, 5),
                Child("C:3", "Tiny", 1, 1),
                Child("C:4", "Denied", 0, 0, NodeState.Error, 5),
            ]);
        m.Nodes["C:1"] = Node("C:1", "Users", @"C:\Users", total: 300, files: 10, dirs: 3, own: 20, ownFiles: 1,
            children:
            [
                Child("C:5", "steve", 200, 6),
                Child("C:6", "other", 80, 3),
            ]);
        m.Scans["C"] = Scan(status: scanState);
        return m;
    }
}
