using DeleteMan.Engine;
using DeleteMan.ViewModels.Tests.TestSupport;

namespace DeleteMan.ViewModels.Tests;

/// <summary>
/// The whole drill-down flattened in display order (model.ts buildRows): drive row, used row, folder rows, files
/// row, the "(small N)" row of a files row and the pages of a folder's leftover ("(small N)" / "(other N)"), with
/// proportional segment layout and folding of tiny segments.
/// </summary>
public class RowsTests
{
    private const double Tolerance = 0.001;

    private static ChainEntry Dir(string id) => new(EntryKind.Dir, id, id);

    [Fact]
    public void Collapsed_drive_is_a_single_drive_row()
    {
        var m = Make.Fixture();
        var rows = Rows.Build(m);
        var row = Assert.Single(rows);
        Assert.Equal("C#-1", row.Key);
        Assert.Equal(RowKind.Drive, row.Kind);
        Assert.Equal("C", row.Letter);
        Assert.Equal(-1, row.Index);
        Assert.Equal("C:  System", row.Label);
        Assert.Equal(@"C:\ System", row.Title);
        Assert.Equal("NTFS", row.Sub);
        Assert.Equal("1000 B", row.Size);
        Assert.Null(row.Edge);
        Assert.Null(row.From);
        Assert.Equal(0.6, row.Fill!.Value, Tolerance);
        Assert.Null(row.Expanded);
        Assert.Empty(row.Controls);
        Assert.Null(row.Deleting);
        Assert.Equal("", row.Placeholder);

        var seg = Assert.Single(row.Segs);
        Assert.Equal("drive", seg.Key);
        Assert.Equal("600 B used · 400 B free", seg.Text);
        Assert.Equal(0, seg.Left, Tolerance);
        Assert.Equal(100, seg.Width, Tolerance);
        Assert.Equal(Palette.DriveTrack, seg.Color);
        Assert.Equal(SegStyle.Drive, seg.Style);
        Assert.True(seg.Expandable);
        Assert.Null(seg.Progress);
        Assert.Equal(new HoverInfo("C:  System", ["Used 600 B · Free 400 B · NTFS"]), seg.Hover);
    }

    [Fact]
    public void Drive_row_details_for_unlabelled_removable_drives()
    {
        var m = new Model { Drives = [Make.Drive("E", label: "", fs: "exFAT", kind: DriveKind.Removable, total: 2000, free: 2000)] };
        var row = Assert.Single(Rows.Build(m));
        Assert.Equal("E:", row.Label);
        Assert.Equal(@"E:\", row.Title);
        Assert.Equal("exFAT · removable", row.Sub);
        Assert.Equal(0.0, row.Fill!.Value, Tolerance);
        Assert.Equal("0 B used · 2.0 KB free", row.Segs[0].Text);
    }

    [Fact]
    public void Used_row_lays_out_children_folds_tiny_ones_and_shows_the_unscanned_remainder()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [Chain.UsedEntry("C:0")];
        var rows = Rows.Build(m);
        Assert.Equal(2, rows.Count);
        Assert.Equal("drive", rows[0].Expanded);
        Assert.Equal([RowControl.Close], rows[0].Controls); // an open drive can be collapsed; a collapsed one has no controls

        var used = rows[1];
        Assert.Equal("C#0", used.Key);
        Assert.Equal(RowKind.Used, used.Kind);
        Assert.Equal(0, used.Index);
        Assert.Equal(@"C:\", used.Label);
        Assert.Equal(@"C:\", used.Title);
        Assert.Equal("scanning… depth 3 · 1,234 folders", used.Sub);
        Assert.Equal("600 B", used.Size);
        Assert.Equal(Palette.Accent, used.Edge);
        Assert.Equal("used", used.From);
        Assert.Null(used.Fill);
        Assert.Equal("", used.Placeholder);
        Assert.Null(used.Expanded);
        Assert.Equal([RowControl.Close], used.Controls);
        Assert.Null(used.Deleting);

        // main: Users, Windows ; tail: Denied (error, not foldable), (files), the leftover "(small 1)", unscanned
        Assert.Equal(["C:1", "C:2", "C:4", "files", "other", "unscanned"], used.Segs.Select(s => s.Key));
        var users = used.Segs[0];
        Assert.Equal("Users", users.Text);
        Assert.Equal(0, users.Left, Tolerance);
        Assert.Equal(50, users.Width, Tolerance);
        Assert.Equal(Palette.ColorFor("Users"), users.Color);
        Assert.Equal(SegStyle.Plain, users.Style);
        Assert.True(users.Expandable);
        Assert.Null(users.Progress);
        Assert.Equal(new HoverInfo(@"C:\Users", ["300 B", "10 files"]), users.Hover);

        var windows = used.Segs[1];
        Assert.Equal(50, windows.Left, Tolerance);
        Assert.Equal(140.0 / 600 * 100, windows.Width, Tolerance);

        var denied = used.Segs[2];
        Assert.Equal("Denied", denied.Text);
        Assert.Equal(0, denied.Width, Tolerance);
        Assert.Equal(Palette.Error, denied.Color);
        Assert.Equal(SegStyle.Error, denied.Style);
        Assert.False(denied.Expandable);
        Assert.Equal(new HoverInfo(@"C:\Denied", ["0 B", "0 files", "Access denied (5)"]), denied.Hover);

        var files = used.Segs[3];
        Assert.Equal("(files)", files.Text);
        Assert.Equal(50.0 / 600 * 100, files.Width, Tolerance);
        Assert.Equal(Palette.Files, files.Color);
        Assert.Equal(SegStyle.Files, files.Style);
        Assert.True(files.Expandable);
        Assert.Equal(new HoverInfo(@"(files) in C:\", ["50 B", "2 files"]), files.Hover);

        // Every root folder was listed, so the leftover is just the folded Tiny and reads "(small 1)"; it opens a page.
        var small = used.Segs[4];
        Assert.Equal("other", small.Key);
        Assert.Equal("(small 1)", small.Text);
        Assert.Equal(1.0 / 600 * 100, small.Width, Tolerance);
        Assert.Equal(Palette.Small, small.Color);
        Assert.Equal(SegStyle.Small, small.Style);
        Assert.True(small.Expandable, "the used row opens its folded folders as a page");
        Assert.Equal(new HoverInfo("(small 1)", ["1 B", "1 folder under 0.25 % of this bar", "Click to open them as a row"]), small.Hover);

        var unscanned = used.Segs[5];
        Assert.Equal("unscanned", unscanned.Text);
        Assert.Equal(100.0 / 600 * 100, unscanned.Width, Tolerance);
        // Segments tile left to right; the scan has accounted 500 B but the root's children + loose files sum to
        // 491 B (live totals), so the bar ends at 591/600 rather than at 100 %.
        Assert.Equal(491.0 / 600 * 100, unscanned.Left, Tolerance);
        Assert.Equal(Palette.Hatch, unscanned.Color);
        Assert.Equal(SegStyle.Hatched, unscanned.Style);
        Assert.False(unscanned.Expandable);
        Assert.Equal(new HoverInfo("unscanned", ["100 B", "Not scanned yet"]), unscanned.Hover);
    }

    [Fact]
    public void Finished_scan_names_the_remainder_other_system()
    {
        var m = Make.Fixture(ScanState.Done);
        m.Chains["C"] = [Chain.UsedEntry("C:0")];
        var used = Rows.Build(m)[1];
        Assert.Equal("scan complete", used.Sub);
        var rest = used.Segs.Single(s => s.Key == "unscanned");
        Assert.Equal("other/system", rest.Text);
        Assert.Equal(new HoverInfo("other/system", ["100 B", "Used space the scan could not attribute (file system metadata, protected folders, shadow copies)"]), rest.Hover);
    }

    [Fact]
    public void Used_row_without_data_yet_shows_a_loading_placeholder()
    {
        var m = Make.Fixture();
        m.Nodes.Clear();
        m.Scans.Clear();
        m.Chains["C"] = [Chain.UsedEntry("C:0")];
        var used = Rows.Build(m)[1];
        Assert.Equal("starting scan…", used.Sub);
        Assert.Empty(used.Segs);
        Assert.Equal("loading…", used.Placeholder);

        m.Scans["C"] = Make.Scan(status: ScanState.Cancelled);
        Assert.Equal("scan cancelled", Rows.Build(m)[1].Sub);
        m.Scans["C"] = Make.Scan(status: ScanState.Idle);
        Assert.Equal("waiting to scan…", Rows.Build(m)[1].Sub);
    }

    [Fact]
    public void Dir_row_has_controls_edge_colour_and_its_own_layout()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1")];
        var rows = Rows.Build(m);
        Assert.Equal(3, rows.Count);
        Assert.Equal("C:1", rows[1].Expanded);

        var dir = rows[2];
        Assert.Equal("C#1", dir.Key);
        Assert.Equal(RowKind.Dir, dir.Kind);
        Assert.Equal(1, dir.Index);
        Assert.Equal("Users", dir.Label);
        Assert.Equal(@"C:\Users", dir.Title);
        Assert.Equal("10 files · 3 folders", dir.Sub);
        Assert.Equal("300 B", dir.Size);
        Assert.Equal(Palette.ColorFor("Users"), dir.Edge);
        Assert.Equal("C:1", dir.From);
        Assert.Equal([RowControl.Close, RowControl.Recycle, RowControl.Delete], dir.Controls);
        Assert.Null(dir.Deleting);
        Assert.Null(dir.Expanded);
        Assert.Equal(["C:5", "C:6", "files"], dir.Segs.Select(s => s.Key));
        Assert.Equal(200.0 / 300 * 100, dir.Segs[0].Width, Tolerance);
        Assert.Equal(80.0 / 300 * 100, dir.Segs[1].Width, Tolerance);
        Assert.Equal(20.0 / 300 * 100, dir.Segs[2].Width, Tolerance);
        Assert.Equal(new HoverInfo(@"C:\Users\steve", ["200 B", "6 files"]), dir.Segs[0].Hover);
    }

    [Fact]
    public void Dir_row_before_its_node_arrives_and_for_special_states()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:9")];
        var pending = Rows.Build(m)[2];
        Assert.Equal("…", pending.Label);
        Assert.Equal("", pending.Title);
        Assert.Equal("\u00A0", pending.Sub);
        Assert.Equal("", pending.Size);
        Assert.Null(pending.Edge);
        Assert.Empty(pending.Segs);
        Assert.Equal("loading…", pending.Placeholder);
        Assert.Equal([RowControl.Close], pending.Controls);

        m.Nodes["C:9"] = Make.Node("C:9", "Empty", @"C:\Empty");
        Assert.Equal("empty folder", Rows.Build(m)[2].Placeholder);

        m.Nodes["C:9"] = Make.Node("C:9", "Locked", @"C:\Locked", state: NodeState.Error, errorCode: 5);
        var error = Rows.Build(m)[2];
        Assert.Equal("Access denied (5)", error.Placeholder);
        Assert.Equal(Palette.Error, error.Edge);

        m.Nodes["C:9"] = Make.Node("C:9", "Later", @"C:\Later", state: NodeState.Pending);
        Assert.Equal("waiting to be scanned", Rows.Build(m)[2].Placeholder);
        m.Nodes["C:9"] = Make.Node("C:9", "Now", @"C:\Now", state: NodeState.Scanning);
        Assert.Equal("scanning…", Rows.Build(m)[2].Placeholder);
    }

    [Fact]
    public void Child_segment_states_map_to_styles()
    {
        var m = Make.Fixture();
        m.Nodes["C:1"] = m.Nodes["C:1"] with
        {
            Children =
            [
                Make.Child("C:5", "steve", 200, 6, NodeState.Scanning),
                Make.Child("C:6", "other", 80, 3, NodeState.Pending),
            ],
        };
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1")];
        var dir = Rows.Build(m)[2];
        Assert.Equal(SegStyle.Pending, dir.Segs[0].Style);
        Assert.Equal(["200 B", "6 files", "scanning…"], dir.Segs[0].Hover.Lines);
        Assert.Equal(SegStyle.Pending, dir.Segs[1].Style);
        Assert.Equal(["80 B", "3 files", "waiting to be scanned"], dir.Segs[1].Hover.Lines);
    }

    [Fact]
    public void Other_segment_holds_the_children_the_engine_did_not_list_and_opens_a_page_of_them()
    {
        var m = Make.Fixture();
        m.Nodes["C:1"] = m.Nodes["C:1"] with { OtherCount = 7, OtherBytes = 30 };
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1")];
        var dir = Rows.Build(m)[2];
        var other = dir.Segs.Single(s => s.Key == "other");
        Assert.Equal("(other 7)", other.Text);
        Assert.Equal(Palette.Other, other.Color);
        Assert.Equal(SegStyle.Other, other.Style);
        Assert.True(other.Expandable, "the unlisted folders open as a page");
        Assert.Equal(new HoverInfo("(other 7)", ["30 B", "7 more folders not shown here", "Click to open them as a row, largest first"]), other.Hover);
        // Non-foldable structural segments come after the children, in order: files, then the leftover.
        Assert.Equal(["C:5", "C:6", "files", "other"], dir.Segs.Select(s => s.Key));
        // The denominator grows to the segment sum when that exceeds the nominal total (300 vs 330).
        Assert.Equal(200.0 / 330 * 100, dir.Segs[0].Width, Tolerance);
    }

    [Fact]
    public void Tiny_children_fold_into_the_other_segment_when_there_is_one()
    {
        var m = Make.Fixture();
        // Users: steve and other draw, dust (1 of 3,000 B) folds, and 7 unlisted folders of 30 B join it: one leftover.
        m.Nodes["C:1"] = m.Nodes["C:1"] with
        {
            Children = [Make.Child("C:5", "steve", 2000, 6), Make.Child("C:6", "other", 800, 3), Make.Child("C:7", "dust", 1, 1)],
            TotalBytes = 3000,
            OtherCount = 7,
            OtherBytes = 30,
        };
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1")];
        var dir = Rows.Build(m)[2];
        Assert.Equal(["C:5", "C:6", "files", "other"], dir.Segs.Select(s => s.Key));
        var other = dir.Segs[3];
        Assert.Equal("(other 8)", other.Text);
        Assert.Equal(31.0 / 3000 * 100, other.Width, Tolerance);
        Assert.Equal(new HoverInfo("(other 8)", ["31 B", "8 more folders not shown here", "Click to open them as a row, largest first"]), other.Hover);

        // Children past the row's window of 64 (listed because a page is open) count as unlisted for the row itself.
        m.Nodes["C:1"] = m.Nodes["C:1"] with
        {
            Children = Enumerable.Range(0, 70).Select(i => Make.Child($"C:{100 + i}", $"d{i:D2}", 10, 1)).ToList(),
            TotalBytes = 700,
            OtherCount = 0,
            OtherBytes = 0,
        };
        var wide = Rows.Build(m)[2];
        Assert.Equal(66, wide.Segs.Count); // 64 children, (files), (other 6)
        Assert.Equal("(other 6)", wide.Segs[^1].Text);
        Assert.Equal(60.0 / 720 * 100, wide.Segs[^1].Width, Tolerance);
    }

    [Fact]
    public void Deleting_row_replaces_controls_with_progress()
    {
        var m = Make.Fixture();
        m.Nodes["C:1"] = m.Nodes["C:1"] with { DeletingJobId = "7" };
        m.Jobs = [Make.Job("7", path: @"C:\Users", nodeId: "C:1")];
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1")];
        var rows = Rows.Build(m);

        var dir = rows[2];
        Assert.Empty(dir.Controls);
        Assert.Equal(new DeletingView("7", 0.25, "Deleting… 25 % · 3 files · 250 B / 1000 B freed · 1:05"), dir.Deleting);

        // The parent row's segment for that folder shows the same progress.
        var users = rows[1].Segs.Single(s => s.Key == "C:1");
        Assert.Equal(new ProgressView(0.25, "Deleting… 25 % · 3 files · 250 B / 1000 B freed · 1:05"), users.Progress);
        Assert.Null(rows[1].Segs.Single(s => s.Key == "C:2").Progress);

        // A job deleting a single file inside the folder does not turn the folder row into a progress bar.
        m.Jobs = [Make.Job("7", path: @"C:\Users\a.bin", nodeId: "C:1")];
        var notDeleting = Rows.Build(m)[2];
        Assert.Null(notDeleting.Deleting);
        Assert.Equal(3, notDeleting.Controls.Count);

        // Path matching is case-insensitive even without the deleting_job_id hint.
        m.Nodes["C:1"] = m.Nodes["C:1"] with { DeletingJobId = null };
        m.Jobs = [Make.Job("8", path: @"c:\users")];
        Assert.Equal("8", Rows.Build(m)[2].Deleting!.JobId);

        // Finished jobs do not count.
        m.Jobs = [Make.Job("8", path: @"C:\Users", status: JobStatus.Done)];
        Assert.Null(Rows.Build(m)[2].Deleting);
    }

    [Fact]
    public void Files_row_lists_loose_files_largest_first_each_opening_as_its_own_row()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1"), new ChainEntry(EntryKind.Files, "C:1", "files")];
        var loading = Rows.Build(m)[3];
        Assert.Equal("C#2", loading.Key);
        Assert.Equal(RowKind.Files, loading.Kind);
        Assert.Equal("(files)", loading.Label);
        Assert.Equal(@"Files directly in C:\Users", loading.Title);
        Assert.Equal("\u00A0", loading.Sub);
        Assert.Equal("0 B", loading.Size);
        Assert.Equal(Palette.Files, loading.Edge);
        Assert.Equal("files", loading.From);
        Assert.Equal("loading…", loading.Placeholder);
        Assert.Null(loading.Expanded);
        Assert.Equal([RowControl.Close], loading.Controls);
        Assert.Equal("files", Rows.Build(m)[2].Expanded);

        m.Files["C:1"] = [Make.File("a.bin", 60), Make.File("b.bin", 40, 0x1 | 0x2), Make.File("c.bin", 0)];
        var files = Rows.Build(m)[3];
        Assert.Equal("3 files", files.Sub);
        Assert.Equal("100 B", files.Size);
        Assert.Equal("", files.Placeholder);
        Assert.Equal(["f:a.bin", "f:b.bin", "small"], files.Segs.Select(s => s.Key));
        var a = files.Segs[0];
        Assert.Equal("a.bin", a.Text);
        Assert.Equal(60, a.Width, Tolerance);
        Assert.Equal(Palette.ColorFor("a.bin"), a.Color);
        Assert.Equal(SegStyle.Plain, a.Style);
        Assert.True(a.Expandable);
        Assert.Equal(new HoverInfo(@"C:\Users\a.bin", ["60 B"]), a.Hover);
        Assert.Equal(new HoverInfo(@"C:\Users\b.bin", ["40 B", "read-only · hidden"]), files.Segs[1].Hover);
        Assert.True(files.Segs[2].Expandable, "the folded files open as a small row");

        m.Files["C:1"] = [];
        var empty = Rows.Build(m)[3];
        Assert.Equal("0 files", empty.Sub);
        Assert.Equal("no loose files", empty.Placeholder);

        m.Files["C:1"] = Enumerable.Range(0, 500).Select(i => Make.File($"f{i}", 1)).ToList();
        Assert.Equal("500 files (largest 500)", Rows.Build(m)[3].Sub);
    }

    [Fact]
    public void Folder_whose_loose_files_amount_to_one_file_shows_it_by_name_in_the_files_colour()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1")];
        // Until the list is here the segment is the usual "(files)".
        var filesSeg = Rows.Build(m)[2].Segs.Single(s => s.Key == "files");
        Assert.Equal("(files)", filesSeg.Text);

        // Users has one loose file: the segment is that file, same place and size, files colour, opening its row.
        m.Files["C:1"] = [Make.File("notes.txt", 20, 0x1)];
        var single = Rows.Build(m)[2].Segs.Single(s => Chain.IsFileKey(s.Key));
        Assert.Equal("f:notes.txt", single.Key);
        Assert.Equal("notes.txt", single.Text);
        Assert.Equal(filesSeg.Left, single.Left, Tolerance);
        Assert.Equal(filesSeg.Width, single.Width, Tolerance);
        Assert.Equal(Palette.Files, single.Color);
        Assert.Equal(SegStyle.Files, single.Style);
        Assert.True(single.Expandable);
        Assert.Equal(new HoverInfo(@"C:\Users\notes.txt", ["20 B", "read-only"]), single.Hover);
        Assert.DoesNotContain(Rows.Build(m)[2].Segs, s => s.Key == "files");

        // The root's two files both show (49 and 1 of 50): "(files)" stays.
        m.Files["C:0"] = [Make.File("pagefile.sys", 49), Make.File("tiny", 1)];
        Assert.Contains(Rows.Build(m)[1].Segs, s => s.Key == "files");

        // One that shows and one that folds: the shown file stands in for both, and says so.
        m.Files["C:0"] = [Make.File("pagefile.sys", 49_999), Make.File("tiny", 1)];
        var root = Rows.Build(m)[1].Segs.Single(s => Chain.IsFileKey(s.Key));
        Assert.Equal("pagefile.sys", root.Text);
        Assert.Equal(
            new HoverInfo(@"C:\pagefile.sys", [Format.Bytes(49_999), "plus 1 smaller file under 0.25 % of the loose bytes"]),
            root.Hover);
    }

    [Fact]
    public void File_row_shows_one_file_at_full_width_with_the_folder_row_controls()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1"), new ChainEntry(EntryKind.Files, "C:1", "files"), new ChainEntry(EntryKind.File, "C:1", "f:b.bin")];
        var loading = Rows.Build(m)[4];
        Assert.Equal("C#3", loading.Key);
        Assert.Equal(RowKind.File, loading.Kind);
        Assert.Equal("b.bin", loading.Label);
        Assert.Equal("\u00A0", loading.Sub);
        Assert.Equal("loading…", loading.Placeholder);
        Assert.Equal(Palette.ColorFor("b.bin"), loading.Edge);
        Assert.Equal("f:b.bin", loading.From);
        Assert.Equal([RowControl.Close], loading.Controls);
        Assert.Equal(PinState.None, loading.Pin);

        m.Files["C:1"] = [Make.File("a.bin", 60), Make.File("b.bin", 40, 0x1 | 0x2)];
        var rows = Rows.Build(m);
        Assert.Equal("f:b.bin", rows[3].Expanded);
        var file = rows[4];
        Assert.Equal(@"C:\Users\b.bin", file.Title);
        Assert.Equal("file · read-only · hidden", file.Sub);
        Assert.Equal("40 B", file.Size);
        Assert.Equal("", file.Placeholder);
        Assert.Equal([RowControl.Close, RowControl.Recycle, RowControl.Delete], file.Controls);
        Assert.Null(file.Deleting);
        var seg = Assert.Single(file.Segs);
        Assert.Equal("file", seg.Key);
        Assert.Equal("b.bin", seg.Text);
        Assert.Equal(0, seg.Left, Tolerance);
        Assert.Equal(100, seg.Width, Tolerance);
        Assert.Equal(Palette.ColorFor("b.bin"), seg.Color);
        Assert.False(seg.Expandable);
        Assert.Equal(new HoverInfo(@"C:\Users\b.bin", ["40 B", "read-only · hidden"]), seg.Hover);

        // A job deleting the file turns the row into its progress bar and takes the controls away.
        m.Jobs = [Make.Job("9", path: @"C:\Users\b.bin", status: JobStatus.Running)];
        var deleting = Rows.Build(m)[4];
        Assert.Equal("9", deleting.Deleting?.JobId);
        Assert.Empty(deleting.Controls);

        // Once the folder's list no longer has the file, the row says so (the controller then closes it).
        m.Jobs = [];
        m.Files["C:1"] = [Make.File("a.bin", 60)];
        Assert.Equal("no longer here", Rows.Build(m)[4].Placeholder);
    }

    [Fact]
    public void Small_row_of_a_files_row_lists_the_folded_files_each_opening_as_its_own_row()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1"), new ChainEntry(EntryKind.Files, "C:1", "files"), new ChainEntry(EntryKind.Small, "C:1", "small")];
        Assert.Equal("loading…", Rows.Build(m)[4].Placeholder);

        m.Files["C:1"] = [Make.File("a.bin", 10_000), Make.File("tiny1", 1), Make.File("tiny2", 2)];
        var rows = Rows.Build(m);
        Assert.Equal("small", rows[3].Expanded);
        var small = rows[4];
        Assert.Equal(RowKind.Small, small.Kind);
        Assert.Equal("(small 2)", small.Label);
        Assert.Equal(@"Files under 0.25 % of C:\Users", small.Title);
        Assert.Equal("2 files under 0.25 % of Users", small.Sub);
        Assert.Equal("3 B", small.Size);
        Assert.Equal(["f:tiny1", "f:tiny2"], small.Segs.Select(s => s.Key));
        Assert.All(small.Segs, s => Assert.True(s.Expandable));
        Assert.Equal(PinState.None, small.Pin);
    }

    [Fact]
    public void Other_row_shows_the_folded_folders_at_their_own_scale()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), new ChainEntry(EntryKind.Other, "C:0", "other")];
        var rows = Rows.Build(m);
        Assert.Equal("other", rows[1].Expanded);
        var small = rows[2];
        Assert.Equal("C#1", small.Key);
        Assert.Equal(RowKind.Other, small.Kind);
        Assert.Equal("(small 1)", small.Label);
        Assert.Equal(@"Folders under 0.25 % of C:\", small.Title);
        Assert.Equal(@"1 folder under 0.25 % of C:\", small.Sub);
        Assert.Equal("1 B", small.Size);
        Assert.Equal(Palette.Small, small.Edge);
        Assert.Equal("other", small.From);
        Assert.Equal([RowControl.Close], small.Controls);
        Assert.Equal(PinState.Off, small.Pin);
        var tiny = Assert.Single(small.Segs);
        Assert.Equal("C:3", tiny.Key);
        Assert.Equal("Tiny", tiny.Text);
        Assert.Equal(100, tiny.Width, Tolerance);
        Assert.True(tiny.Expandable);
        Assert.Equal("", small.Placeholder);

        // Below a folder row the same scale rules apply to that folder's children (1 B of 1,019 B is under 0.25 %).
        m.Nodes["C:1"] = m.Nodes["C:1"] with { Children = [Make.Child("C:5", "steve", 998, 6), Make.Child("C:6", "dust", 1, 1)], TotalBytes = 1000 };
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1"), new ChainEntry(EntryKind.Other, "C:1", "other")];
        var fromDir = Rows.Build(m)[3];
        Assert.Equal("(small 1)", fromDir.Label);
        Assert.Equal("1 folder under 0.25 % of Users", fromDir.Sub);
        Assert.Equal("dust", Assert.Single(fromDir.Segs).Text);

        // Without data yet.
        m.Nodes.Remove("C:1");
        var waiting = Rows.Build(m)[3];
        Assert.Equal("(other)", waiting.Label);
        Assert.Equal("\u00A0", waiting.Sub);
        Assert.Equal("loading…", waiting.Placeholder);
    }

    [Fact]
    public void Other_row_pages_through_the_unlisted_folders_largest_first()
    {
        var m = Make.Fixture();
        // Users has 130 subfolders of 10 B; its row draws 64, so 66 are left over: two pages' worth. What the engine
        // lists depends on the window asked for (64 for the row alone, 128 with one page open, 192 with two:
        // Model.NodeRequests); the node views below are what each poll would bring.
        var kids = Enumerable.Range(0, 130).Select(i => Make.Child($"C:{100 + i}", $"d{i:D3}", 10, 1)).ToList();
        m.Nodes["C:1"] = Make.Node("C:1", "Users", @"C:\Users", total: 1300, files: 130, dirs: 130, children: kids.Take(64).ToList(), otherCount: 66, otherBytes: 660);
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1"), new ChainEntry(EntryKind.Other, "C:1", "other")];
        Assert.Equal([new NodeRequest("C:0", 64), new NodeRequest("C:1", 128)], m.NodeRequests());

        // Until the wider window arrives the page has nothing to draw.
        var loading = Rows.Build(m)[3];
        Assert.Equal("(other 66)", loading.Label);
        Assert.Equal("660 B", loading.Size);
        Assert.Empty(loading.Segs);
        Assert.Equal("loading…", loading.Placeholder);

        m.Nodes["C:1"] = m.Nodes["C:1"] with { Children = kids.Take(128).ToList(), OtherCount = 2, OtherBytes = 20 };
        var rows = Rows.Build(m);
        Assert.Equal(65, rows[2].Segs.Count); // the row still draws its 64 largest; the 64 listed beyond them are leftover
        Assert.Equal("(other 66)", rows[2].Segs[^1].Text);
        Assert.Equal("other", rows[2].Expanded);
        var page = rows[3];
        Assert.Equal(RowKind.Other, page.Kind);
        Assert.Equal("(other 66)", page.Label);
        Assert.Equal(@"Folders in C:\Users not shown on its row, largest first", page.Title);
        Assert.Equal("Folders 65–128 of 130 in Users, largest first", page.Sub);
        Assert.Equal("660 B", page.Size);
        Assert.Equal(Palette.Other, page.Edge);
        Assert.Equal(65, page.Segs.Count);
        // The 64 folders share 96 % of the bar equally; the 2 beyond the page get the 4 % floor (20 of 660 B is 3 %).
        Assert.Equal("d064", page.Segs[0].Text);
        Assert.Equal(1.5, page.Segs[0].Width, Tolerance);
        Assert.Equal("d127", page.Segs[63].Text);
        var more = page.Segs[64];
        Assert.Equal("other", more.Key);
        Assert.Equal("(other 2)", more.Text);
        Assert.Equal(96, more.Left, Tolerance);
        Assert.Equal(4, more.Width, Tolerance);
        Assert.Equal(new HoverInfo("(other 2)", ["20 B", "2 more folders not shown here", "Click to open them as a row, largest first"]), more.Hover);

        // The next page is another "other" entry of the same node; it asks for a wider window again.
        m.Chains["C"] = [.. m.Chains["C"], new ChainEntry(EntryKind.Other, "C:1", "other")];
        Assert.Equal([new NodeRequest("C:0", 64), new NodeRequest("C:1", 192)], m.NodeRequests());
        m.Nodes["C:1"] = m.Nodes["C:1"] with { Children = kids, OtherCount = 0, OtherBytes = 0 };
        rows = Rows.Build(m);
        Assert.Equal("other", rows[3].Expanded);
        var last = rows[4];
        Assert.Equal("(other 2)", last.Label);
        Assert.Equal("Folders 129–130 of 130 in Users, largest first", last.Sub);
        Assert.Equal("20 B", last.Size);
        Assert.Equal(["C:228", "C:229"], last.Segs.Select(s => s.Key));
        Assert.Equal(50, last.Segs[0].Width, Tolerance);
        Assert.Equal(50, last.Segs[1].Width, Tolerance);
    }

    [Fact]
    public void A_page_folds_its_own_tiny_folders_on_to_the_next_page_and_draws_empty_ones_equally()
    {
        var m = Make.Fixture();
        // Users: steve draws; mid (2,000 of 1,002,000 B) and ten empty folders fold, and nothing is unlisted.
        var kids = new List<ChildView> { Make.Child("C:5", "steve", 1_000_000, 6), Make.Child("C:6", "mid", 2000, 1) };
        kids.AddRange(Enumerable.Range(0, 10).Select(i => Make.Child($"C:{10 + i}", $"empty{i}", 0, 0)));
        m.Nodes["C:1"] = Make.Node("C:1", "Users", @"C:\Users", total: 1_002_000, files: 7, dirs: 12, children: kids);
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1"), new ChainEntry(EntryKind.Other, "C:1", "other")];
        var rows = Rows.Build(m);
        Assert.Equal(["C:5", "other"], rows[2].Segs.Select(s => s.Key));
        Assert.Equal("(small 11)", rows[2].Segs[1].Text);

        // Page 1: mid is the page; the empty folders are under 0.25 % of it and fold on, as "(small 10)" since
        // nothing is unlisted. With no bytes, the fold gets the 4 % floor.
        var page = rows[3];
        Assert.Equal("(small 11)", page.Label);
        Assert.Equal("11 folders under 0.25 % of Users", page.Sub);
        Assert.Equal("2.0 KB", page.Size);
        Assert.Equal(["C:6", "other"], page.Segs.Select(s => s.Key));
        Assert.Equal(96, page.Segs[0].Width, Tolerance);
        Assert.Equal("(small 10)", page.Segs[1].Text);
        Assert.Equal(4, page.Segs[1].Width, Tolerance);
        Assert.Equal(Palette.Small, page.Segs[1].Color);

        // Page 2: ten empty folders, drawn at equal widths since there is nothing to tell them apart by.
        m.Chains["C"] = [.. m.Chains["C"], new ChainEntry(EntryKind.Other, "C:1", "other")];
        var last = Rows.Build(m)[4];
        Assert.Equal("(small 10)", last.Label);
        Assert.Equal("Folders 3–12 of 12 in Users, largest first", last.Sub);
        Assert.Equal("0 B", last.Size);
        Assert.Equal(10, last.Segs.Count);
        Assert.All(last.Segs, s => Assert.Equal(10, s.Width, Tolerance));
        Assert.Equal(PinState.Off, last.Pin);
    }

    [Fact]
    public void Layout_denominator_is_at_least_one_and_segments_tile_left_to_right()
    {
        var m = Make.Fixture();
        m.Nodes["C:1"] = Make.Node("C:1", "Users", @"C:\Users", children: [Make.Child("C:5", "a", 0, 0), Make.Child("C:6", "b", 0, 0)]);
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1")];
        var dir = Rows.Build(m)[2];
        Assert.All(dir.Segs, s => Assert.Equal(0, s.Width, Tolerance));
        Assert.Equal("0 B", dir.Size);

        var used = Rows.Build(m)[1];
        var left = 0.0;
        foreach (var s in used.Segs)
        {
            Assert.Equal(left, s.Left, Tolerance);
            left += s.Width;
        }
        Assert.True(left <= 100 + Tolerance, $"segments overflow the bar: {left}");
        Assert.Equal(591.0 / 600 * 100, left, Tolerance);
    }

    [Fact]
    public void Rows_from_identical_state_are_equal_so_the_renderer_can_skip_them()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1")];
        var first = Rows.Build(m);
        var second = Rows.Build(m);
        Assert.Equal(first, second);
        Assert.Equal(first[1].GetHashCode(), second[1].GetHashCode());

        // Only the folder row reads its own node; the drive and used rows are untouched.
        m.Nodes["C:1"] = m.Nodes["C:1"] with { TotalBytes = 301 };
        var changed = Rows.Build(m);
        Assert.Equal(first[0], changed[0]);
        Assert.Equal(first[1], changed[1]);
        Assert.NotEqual(first[2], changed[2]);
        Assert.Equal("301 B", changed[2].Size);
    }

    [Fact]
    public void Multiple_drives_keep_their_own_chains_in_drive_order()
    {
        var m = Make.Fixture();
        m.Drives = [Make.Drive("C"), Make.Drive("D", label: "Data", total: 500, free: 100)];
        m.Nodes["D:0"] = Make.Node("D:0", @"D:\", @"D:\", isRoot: true, total: 350, files: 1, dirs: 1, children: [Make.Child("D:1", "Games", 350, 1)]);
        m.Scans["D"] = Make.Scan("D", ScanState.Done, accounted: 350);
        m.Chains["D"] = [Chain.UsedEntry("D:0")];
        var rows = Rows.Build(m);
        Assert.Equal(["C#-1", "D#-1", "D#0"], rows.Select(r => r.Key));
        Assert.Equal("D", rows[2].Letter);
        Assert.Equal("400 B", rows[2].Size);
        Assert.Equal(["D:1", "unscanned"], rows[2].Segs.Select(s => s.Key));
        Assert.Equal("other/system", rows[2].Segs[1].Text);
    }

    [Fact]
    public void Building_again_from_the_same_data_reuses_the_cached_segments_and_gives_equal_rows()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [Chain.UsedEntry("C:0"), Dir("C:1")];
        var first = Rows.Build(m);
        Assert.True(m.SegCache.ContainsKey("C:0"));
        Assert.True(m.SegCache.ContainsKey("C:1"));
        var cachedUsers = m.SegCache["C:1"];

        var second = Rows.Build(m);
        Assert.Equal(first, second);
        Assert.Same(cachedUsers, m.SegCache["C:1"]);

        // A new node view for Users (a child grew) rebuilds its segments; the root's stay cached.
        var cachedRoot = m.SegCache["C:0"];
        m.Nodes["C:1"] = Make.Node("C:1", "Users", @"C:\Users", total: 310, files: 10, dirs: 3, own: 20, ownFiles: 1,
            children: [Make.Child("C:5", "steve", 210, 6), Make.Child("C:6", "other", 80, 3)]);
        var third = Rows.Build(m);
        Assert.NotEqual(second[2], third[2]);
        Assert.Equal(second[1], third[1]);
        Assert.NotSame(cachedUsers, m.SegCache["C:1"]);
        Assert.Same(cachedRoot, m.SegCache["C:0"]);
        Assert.Equal("210 B", third[2].Segs[0].Hover.Lines[0]);

        // Job progress is part of a segment, so a new job list rebuilds the segments too.
        m.Jobs = [Make.Job(path: @"C:\Users\steve")];
        var fourth = Rows.Build(m);
        Assert.NotNull(fourth[2].Segs[0].Progress);
    }
}
