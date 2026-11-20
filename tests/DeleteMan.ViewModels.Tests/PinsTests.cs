using DeleteMan.Engine;
using DeleteMan.ViewModels.Tests.TestSupport;

namespace DeleteMan.ViewModels.Tests;

/// <summary>
/// The pin toggle: a pinned row keeps the largest folder open at every level below it, as far as the scan has
/// got. A drive has one pin at a time (pinning another row moves it there); pinning takes over whatever is open
/// below the row; opening a row by hand below the pin releases it, as does closing a row the pin opened.
/// </summary>
public class PinsTests
{
    private static readonly ChainEntry Used = Chain.UsedEntry("C:0");
    private static readonly ChainEntry PinnedUsed = Used with { Pinned = true };

    private static ChainEntry Dir(string id) => new(EntryKind.Dir, id, id);

    private static ChainEntry Auto(string id) => Dir(id) with { Auto = true };

    private static ChainEntry Other(string parentId) => new(EntryKind.Other, parentId, "other", Auto: true);

    /// <summary>The fixture with Windows grown past Users (the scan found more under it).</summary>
    private static void WindowsOvertakesUsers(Model m)
    {
        m.Nodes["C:0"] = Make.Node("C:0", @"C:\", @"C:\", isRoot: true, total: 760, files: 18, dirs: 4, own: 50, ownFiles: 2,
            children:
            [
                Make.Child("C:2", "Windows", 400, 5),
                Make.Child("C:1", "Users", 300, 10),
                Make.Child("C:3", "Tiny", 1, 1),
                Make.Child("C:4", "Denied", 0, 0, NodeState.Error, 5),
            ]);
    }

    [Fact]
    public void Pin_on_the_root_opens_the_largest_folder_at_each_scanned_level()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [PinnedUsed];

        Assert.True(Pins.Apply(m));
        // Users (300) is the largest root folder; under it steve (200) is the largest; steve's node is not loaded
        // yet, so the drill-down stops there until the next poll brings it.
        Assert.Equal([PinnedUsed, Auto("C:1"), Auto("C:5")], m.Chains["C"]);

        // Nothing to do until the data changes.
        Assert.False(Pins.Apply(m));
        Assert.Equal([PinnedUsed, Auto("C:1"), Auto("C:5")], m.Chains["C"]);
    }

    [Fact]
    public void The_followed_folder_switches_when_another_one_grows_past_it()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [PinnedUsed, Auto("C:1"), Auto("C:5")];

        WindowsOvertakesUsers(m);
        Assert.True(Pins.Apply(m));
        Assert.Equal([PinnedUsed, Auto("C:2")], m.Chains["C"]);
    }

    [Fact]
    public void Pinning_a_deeper_row_moves_the_drives_pin_to_it()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [PinnedUsed, Auto("C:1"), Auto("C:5")];

        // Pin Users (the row the root pin opened): the root pin goes, Users is the one pin and is in charge.
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], 1);
        var usersPinned = Auto("C:1") with { Pinned = true };
        Assert.Equal([Used with { Auto = true }, usersPinned, Auto("C:5")], m.Chains["C"]);
        Assert.Equal(PinState.Off, Pins.StateOf(m.Chains["C"], 0));
        Assert.Equal(PinState.On, Pins.StateOf(m.Chains["C"], 1));

        // Windows overtakes Users: Users stays open, nothing above the pin follows the change.
        WindowsOvertakesUsers(m);
        Assert.False(Pins.Apply(m));
        Assert.Equal([Used with { Auto = true }, usersPinned, Auto("C:5")], m.Chains["C"]);

        // Unpin Users: no pin is left on the drive, so nothing moves.
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], 1);
        Assert.Equal([Used with { Auto = true }, Auto("C:1"), Auto("C:5")], m.Chains["C"]);
        Assert.False(Pins.Apply(m));
    }

    [Fact]
    public void Pinning_a_row_above_the_pin_moves_it_up_and_takes_over_the_rows_below()
    {
        var m = Make.Fixture();
        // The user drilled into Users by hand and pinned it; now they pin the root instead.
        var usersPinned = Dir("C:1") with { Pinned = true };
        m.Chains["C"] = [Used, usersPinned, Auto("C:5")];
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], 0);
        Assert.Equal([PinnedUsed, Auto("C:1"), Auto("C:5")], m.Chains["C"]);
        Assert.Equal(PinState.On, Pins.StateOf(m.Chains["C"], 0));
        Assert.Equal(PinState.Off, Pins.StateOf(m.Chains["C"], 1));

        // The formerly pinned Users row was handed to the new pin: it follows the largest folder now.
        WindowsOvertakesUsers(m);
        Assert.True(Pins.Apply(m));
        Assert.Equal([PinnedUsed, Auto("C:2")], m.Chains["C"]);
    }

    [Fact]
    public void Pinning_a_row_above_a_hand_opened_folder_takes_over_and_opens_the_largest()
    {
        var m = Make.Fixture();
        // The user opened Windows (not the largest) by hand, then pins the root above it.
        m.Chains["C"] = [Used, Dir("C:2")];
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], 0);
        Assert.Equal([PinnedUsed, Auto("C:2")], m.Chains["C"]);
        Assert.Equal(PinState.On, Pins.StateOf(m.Chains["C"], 0));

        // The pin replaces it with Users, the largest, and drills on from there.
        Assert.True(Pins.Apply(m));
        Assert.Equal([PinnedUsed, Auto("C:1"), Auto("C:5")], m.Chains["C"]);
    }

    [Fact]
    public void Unpinning_a_row_opened_by_hand_marks_it_as_pin_opened()
    {
        var m = Make.Fixture();
        // The user drilled into Users by hand, pinned it, then unpinned it: no pin is left...
        m.Chains["C"] = [Used, Dir("C:1")];
        m.Chains["C"] = Pins.Toggle(Pins.Toggle(m.Chains["C"], 1), 1);
        Assert.Equal([Used, Auto("C:1")], m.Chains["C"]);
        Assert.False(Pins.Apply(m));

        // ...and a pin set on the root afterwards may replace Users, which is no longer the user's own row.
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], 0);
        WindowsOvertakesUsers(m);
        Assert.True(Pins.Apply(m));
        Assert.Equal([PinnedUsed, Auto("C:2")], m.Chains["C"]);
    }

    [Fact]
    public void Opening_a_folder_by_hand_below_the_pin_releases_it()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [PinnedUsed, Auto("C:1"), Auto("C:5")];

        // Click "other" (not the largest) on the Users row: the click holds and the pin simply turns off.
        m.Chains["C"] = Pins.Expand(m.Chains["C"], 1, Dir("C:6"));
        Assert.Equal([Used with { Auto = true }, Auto("C:1"), Dir("C:6")], m.Chains["C"]);
        Assert.Equal(PinState.Off, Pins.StateOf(m.Chains["C"], 0));
        Assert.Equal(PinState.Off, Pins.StateOf(m.Chains["C"], -1));

        // Nothing moves on its own any more, even when the largest changes...
        WindowsOvertakesUsers(m);
        Assert.False(Pins.Apply(m));
        Assert.Equal([Used with { Auto = true }, Auto("C:1"), Dir("C:6")], m.Chains["C"]);

        // ...until the root is pinned again, which takes the rows over and follows the new largest.
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], 0);
        Assert.Equal(PinState.On, Pins.StateOf(m.Chains["C"], 0));
        Assert.True(Pins.Apply(m));
        Assert.Equal([Used with { Pinned = true, Auto = true }, Auto("C:2")], m.Chains["C"]);
    }

    [Fact]
    public void Closing_a_row_the_pin_opened_releases_the_pins_above_it()
    {
        var m = Make.Fixture();
        var usersPinned = Auto("C:1") with { Pinned = true };
        m.Chains["C"] = [PinnedUsed, usersPinned, Auto("C:5")];

        // ✕ on steve, which the Users pin opened: both pins go, and the rows stay as they are.
        m.Chains["C"] = Pins.Close(m.Chains["C"], 2);
        Assert.Equal([Used with { Auto = true }, Auto("C:1")], m.Chains["C"]);
        Assert.Equal(PinState.Off, Pins.StateOf(m.Chains["C"], 0));
        Assert.False(Pins.Apply(m));

        // The same through the segment: clicking the expanded (pin-opened) segment collapses it and releases.
        m.Chains["C"] = [PinnedUsed, Auto("C:1"), Auto("C:5")];
        m.Chains["C"] = Pins.Expand(m.Chains["C"], 0, Dir("C:1"));
        Assert.Equal([Used with { Auto = true }], m.Chains["C"]);
    }

    [Fact]
    public void Closing_a_hand_opened_row_keeps_the_pins()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [PinnedUsed, Dir("C:1"), Dir("C:5")];
        m.Chains["C"] = Pins.Close(m.Chains["C"], 1);
        Assert.Equal([PinnedUsed], m.Chains["C"]);
        Assert.True(Pins.Apply(m));
        Assert.Equal([PinnedUsed, Auto("C:1"), Auto("C:5")], m.Chains["C"]);
    }

    [Fact]
    public void Hand_opened_rows_are_opened_as_the_users_own_and_release_the_pin_above()
    {
        var chain = Pins.Expand([PinnedUsed], 0, Auto("C:1"));
        Assert.Equal([Used with { Auto = true }, Dir("C:1")], chain);
        Assert.Equal([Used with { Auto = true }, Dir("C:1"), new ChainEntry(EntryKind.Files, "C:1", "files")],
            Pins.Expand(chain, 1, Chain.EntryFor(chain[1], "files")));
    }

    [Fact]
    public void When_every_folder_is_folded_the_pin_goes_through_the_leftover_group()
    {
        var m = new Model { Drives = [Make.Drive()] };
        // 600 B of folder space (no loose files) in two folders of 1 B: both are under 0.25 % of the bar and fold
        // into "(small 2)", which is then the only thing to follow; its page opens the first of them.
        m.Nodes["C:0"] = Make.Node("C:0", @"C:\", @"C:\", isRoot: true, total: 600, files: 2, dirs: 2, own: 0, ownFiles: 0,
            children: [Make.Child("C:7", "a", 1, 1), Make.Child("C:8", "b", 1, 1)]);
        m.Scans["C"] = Make.Scan(status: ScanState.Done, accounted: 600);
        m.Chains["C"] = [PinnedUsed];

        Assert.True(Pins.Apply(m));
        Assert.Equal([PinnedUsed, Other("C:0"), Auto("C:7")], m.Chains["C"]);
    }

    [Fact]
    public void The_pin_goes_through_the_leftover_page_and_waits_for_its_folders_to_be_listed()
    {
        var m = new Model { Drives = [Make.Drive(total: 2000, free: 1000)] };
        // 1,000 root folders of 1 B, none over 0.25 % of the bar: "(other 1,000)" is all there is to follow. Its page
        // has nothing to draw until the engine lists the folders (the model asks for them: Model.NodeRequests).
        m.Nodes["C:0"] = Make.Node("C:0", @"C:\", @"C:\", isRoot: true, total: 1000, files: 1000, dirs: 1000, otherCount: 1000, otherBytes: 1000);
        m.Scans["C"] = Make.Scan(status: ScanState.Done, accounted: 1000);
        m.Chains["C"] = [PinnedUsed];
        Assert.True(Pins.Apply(m));
        Assert.Equal([PinnedUsed, Other("C:0")], m.Chains["C"]);
        Assert.False(Pins.Apply(m));

        // Listed (64 of them, the page's worth), the first opens: all equal, so the first wins.
        var kids = Enumerable.Range(1, 64).Select(i => Make.Child($"C:{i}", $"d{i:D3}", 1, 1)).ToList();
        m.Nodes["C:0"] = m.Nodes["C:0"] with { Children = kids, OtherCount = 936, OtherBytes = 936 };
        Assert.True(Pins.Apply(m));
        Assert.Equal([PinnedUsed, Other("C:0"), Auto("C:1")], m.Chains["C"]);
    }

    [Fact]
    public void Nothing_opens_until_some_folder_has_bytes()
    {
        var m = new Model { Drives = [Make.Drive()] };
        m.Nodes["C:0"] = Make.Node("C:0", @"C:\", @"C:\", isRoot: true, dirs: 2,
            children: [Make.Child("C:1", "Users", 0, 0, NodeState.Pending), Make.Child("C:2", "Windows", 0, 0, NodeState.Pending)]);
        m.Scans["C"] = Make.Scan();
        m.Chains["C"] = [PinnedUsed];

        Assert.False(Pins.Apply(m));
        Assert.Equal([PinnedUsed], m.Chains["C"]);

        // A row whose node has not arrived yet is left alone as well.
        m.Chains["C"] = [PinnedUsed, Auto("C:1")];
        Assert.False(Pins.Apply(m));
    }

    [Fact]
    public void Unreadable_folders_and_other_are_never_followed_but_the_loose_files_are_when_largest()
    {
        var m = new Model { Drives = [Make.Drive()] };
        m.Nodes["C:0"] = Make.Node("C:0", @"C:\", @"C:\", isRoot: true, total: 600, dirs: 2, own: 250, ownFiles: 5,
            children:
            [
                Make.Child("C:4", "Denied", 300, 0, NodeState.Error, 5),
                Make.Child("C:1", "Users", 50, 10),
            ],
            otherCount: 3, otherBytes: 100);
        m.Scans["C"] = Make.Scan(status: ScanState.Done, accounted: 600);
        m.Chains["C"] = [PinnedUsed];

        // Not Denied (unreadable), not "(other 3)": "(files)" (250) outweighs Users (50), so the files row opens...
        Assert.True(Pins.Apply(m));
        var filesRow = new ChainEntry(EntryKind.Files, "C:0", "files", Auto: true);
        Assert.Equal([PinnedUsed, filesRow], m.Chains["C"]);

        // ...and once its list is here, the largest file opens as its own row.
        m.Files["C:0"] = [Make.File("big.iso", 200), Make.File("notes.txt", 50)];
        Assert.True(Pins.Apply(m));
        Assert.Equal([PinnedUsed, filesRow, new ChainEntry(EntryKind.File, "C:0", "f:big.iso", Auto: true)], m.Chains["C"]);
        Assert.False(Pins.Apply(m)); // a file row is the end of the line

        // Users grows past the loose files: the pin switches to the folder.
        m.Nodes["C:0"] = Make.Node("C:0", @"C:\", @"C:\", isRoot: true, total: 950, dirs: 2, own: 250, ownFiles: 5,
            children:
            [
                Make.Child("C:4", "Denied", 300, 0, NodeState.Error, 5),
                Make.Child("C:1", "Users", 400, 10),
            ],
            otherCount: 3, otherBytes: 100);
        Assert.True(Pins.Apply(m));
        Assert.Equal([PinnedUsed, Auto("C:1")], m.Chains["C"]);
    }

    [Fact]
    public void A_single_loose_file_that_is_the_largest_item_opens_straight_into_its_row()
    {
        var m = new Model { Drives = [Make.Drive()] };
        m.Nodes["C:0"] = Make.Node("C:0", @"C:\", @"C:\", isRoot: true, total: 600, dirs: 1, own: 500, ownFiles: 1,
            children: [Make.Child("C:1", "Users", 100, 10)]);
        m.Scans["C"] = Make.Scan(status: ScanState.Done, accounted: 600);
        m.Files["C:0"] = [Make.File("pagefile.sys", 500)];
        m.Chains["C"] = [PinnedUsed];

        // The root shows the file by name in place of "(files)"; the pin opens it directly, no files row between.
        Assert.True(Pins.Apply(m));
        Assert.Equal([PinnedUsed, new ChainEntry(EntryKind.File, "C:0", "f:pagefile.sys", Auto: true)], m.Chains["C"]);
    }

    [Fact]
    public void Pin_states_of_the_rows()
    {
        var usersPinned = Auto("C:1") with { Pinned = true };
        IReadOnlyList<ChainEntry> chain = [Used with { Auto = true }, usersPinned, Auto("C:5"), new ChainEntry(EntryKind.Files, "C:5", "files", Auto: true)];
        Assert.Equal(1, Pins.Deepest(chain));
        Assert.Equal(PinState.Off, Pins.StateOf(chain, -1)); // the drive row's own pin is not set here
        Assert.Equal(PinState.Off, Pins.StateOf(chain, 0));
        Assert.Equal(PinState.On, Pins.StateOf(chain, 1));
        Assert.Equal(PinState.Off, Pins.StateOf(chain, 2));
        Assert.Equal(PinState.None, Pins.StateOf(chain, 3)); // a files row has no folders to follow

        Assert.Equal(PinState.Off, Pins.StateOf([], -1)); // a collapsed drive row offers the pin
        Assert.Equal(Pins.NoPin, Pins.Deepest([Used, Dir("C:1")]));
    }

    [Fact]
    public void The_drive_rows_pin_is_its_own_and_owns_the_root_row()
    {
        var m = Make.Fixture();
        // The user opened Windows (not the largest) by hand, then pins the drive row.
        m.Chains["C"] = [Used, Dir("C:2")];
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], -1);
        var driveUsed = Used with { DrivePin = true, Auto = true };
        Assert.Equal([driveUsed, Auto("C:2")], m.Chains["C"]);
        Assert.Equal(Pins.DriveIndex, Pins.Deepest(m.Chains["C"]));
        Assert.Equal(PinState.On, Pins.StateOf(m.Chains["C"], -1));
        Assert.Equal(PinState.Off, Pins.StateOf(m.Chains["C"], 0)); // the root row's pin is independent

        // It follows the largest folders from the root down.
        Assert.True(Pins.Apply(m));
        Assert.Equal([driveUsed, Auto("C:1"), Auto("C:5")], m.Chains["C"]);

        // A hand-opened row below releases it; the drive row's button sets it again.
        m.Chains["C"] = Pins.Expand(m.Chains["C"], 1, Dir("C:6"));
        Assert.Equal([Used with { Auto = true }, Auto("C:1"), Dir("C:6")], m.Chains["C"]);
        Assert.Equal(PinState.Off, Pins.StateOf(m.Chains["C"], -1));
        m.Chains["C"] = Pins.Close(m.Chains["C"], 2);
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], -1);
        Assert.Equal([driveUsed, Auto("C:1")], m.Chains["C"]);
        Assert.Equal(PinState.On, Pins.StateOf(m.Chains["C"], -1));

        // Pinning the root row moves the drive's one pin there, and the drive row's button takes it back.
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], 0);
        Assert.Equal([Used with { Pinned = true, Auto = true }, Auto("C:1")], m.Chains["C"]);
        Assert.Equal(PinState.Off, Pins.StateOf(m.Chains["C"], -1));
        Assert.Equal(PinState.On, Pins.StateOf(m.Chains["C"], 0));
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], -1);
        Assert.Equal([driveUsed, Auto("C:1")], m.Chains["C"]);

        // Unpinning the drive leaves the rows where they are, as pin-opened.
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], -1);
        Assert.Equal([Used with { Auto = true }, Auto("C:1")], m.Chains["C"]);
        Assert.Equal(PinState.Off, Pins.StateOf(m.Chains["C"], -1));
        Assert.False(Pins.Apply(m));

        // Collapsing a row the drive's pin opened releases that pin too.
        m.Chains["C"] = Pins.Toggle(m.Chains["C"], -1);
        m.Chains["C"] = Pins.Expand(m.Chains["C"], 0, Dir("C:1"));
        Assert.Equal([Used with { Auto = true }], m.Chains["C"]);
    }

    [Fact]
    public void Rows_without_folders_below_them_have_no_pin()
    {
        var files = new ChainEntry(EntryKind.Files, "C:1", "files");
        var smallFiles = new ChainEntry(EntryKind.Small, "C:1", "small");
        var file = new ChainEntry(EntryKind.File, "C:1", "f:a.bin");
        IReadOnlyList<ChainEntry> chain = [PinnedUsed, Dir("C:1"), files, smallFiles, file];
        Assert.Equal(PinState.None, Pins.StateOf(chain, 2)); // files row
        Assert.Equal(PinState.None, Pins.StateOf(chain, 3)); // the small group of a files row holds files, not folders
        Assert.Equal(PinState.None, Pins.StateOf(chain, 4)); // file row
        Assert.Same(chain, Pins.Toggle(chain, 3));
        Assert.Same(chain, Pins.Toggle(chain, 4));

        // A page of a folder row's leftover does hold folders.
        IReadOnlyList<ChainEntry> folders = [PinnedUsed, Dir("C:1"), new ChainEntry(EntryKind.Other, "C:1", "other")];
        Assert.Equal(PinState.Off, Pins.StateOf(folders, 2));
    }

    [Fact]
    public void Rows_carry_their_pin_state()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [PinnedUsed, Auto("C:1"), new ChainEntry(EntryKind.Files, "C:1", "files", Auto: true)];
        var rows = Rows.Build(m);
        // The drive row's own pin is off (only the root row is pinned); files rows have none.
        Assert.Equal([PinState.Off, PinState.On, PinState.Off, PinState.None], rows.Select(r => r.Pin).ToList());

        // A row being deleted shows Cancel instead of its controls, pin included.
        m.Jobs = [Make.Job(path: @"C:\Users", nodeId: "C:1")];
        rows = Rows.Build(m);
        Assert.NotNull(rows[2].Deleting);
        Assert.Equal(PinState.None, rows[2].Pin);
    }

    [Fact]
    public void Apply_and_build_returns_the_rows_of_the_final_state()
    {
        var m = Make.Fixture();
        m.Chains["C"] = [PinnedUsed];
        var rows = Pins.ApplyAndBuild(m, out var changed);
        Assert.True(changed);
        Assert.Equal([PinnedUsed, Auto("C:1"), Auto("C:5")], m.Chains["C"]);
        Assert.Equal(Rows.Build(m), rows);

        rows = Pins.ApplyAndBuild(m, out changed);
        Assert.False(changed);
        Assert.Equal(Rows.Build(m), rows);
    }
}
