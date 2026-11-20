namespace DeleteMan.ViewModels.Tests;

/// <summary>Pure chain transitions (model.ts): the rows expanded below a drive, top to bottom.</summary>
public class ChainTests
{
    private static readonly ChainEntry Used = Chain.UsedEntry("C:0");
    private static ChainEntry Dir(string id) => new(EntryKind.Dir, id, id);

    [Fact]
    public void Row_keys_and_used_entry()
    {
        Assert.Equal("C#-1", Chain.RowPosKey("C", -1));
        Assert.Equal("D#3", Chain.RowPosKey("D", 3));
        Assert.Equal(new ChainEntry(EntryKind.Used, "C:0", "used"), Used);
        Assert.Equal(64, Chain.MaxChildren);
    }

    [Fact]
    public void Expand_at_appends_replaces_or_collapses()
    {
        var one = Chain.ExpandAt([Used], 0, Dir("C:1"));
        Assert.Equal([Used, Dir("C:1")], one);

        // Clicking the segment that is already expanded collapses it.
        Assert.Equal([Used], Chain.ExpandAt(one, 0, Dir("C:1")));

        // Expanding a different segment replaces everything deeper.
        var deep = Chain.ExpandAt(one, 1, Dir("C:5"));
        Assert.Equal([Used, Dir("C:1"), Dir("C:5")], deep);
        Assert.Equal([Used, Dir("C:2")], Chain.ExpandAt(deep, 0, Dir("C:2")));

        // A files row keyed on the parent node.
        var files = new ChainEntry(EntryKind.Files, "C:1", "files");
        Assert.Equal([Used, Dir("C:1"), files], Chain.ExpandAt(one, 1, files));
    }

    [Fact]
    public void Entry_for_a_segment_opens_a_folder_the_files_a_leftover_group_or_one_file()
    {
        var parent = Dir("C:1");
        Assert.Equal(Dir("C:5"), Chain.EntryFor(parent, "C:5"));
        Assert.Equal(new ChainEntry(EntryKind.Files, "C:1", "files"), Chain.EntryFor(parent, "files"));
        Assert.Equal(new ChainEntry(EntryKind.Small, "C:1", "small"), Chain.EntryFor(parent, "small"));

        // A folder's leftover page keeps the folder's node, as does the next page opened from a page's own leftover.
        var page = new ChainEntry(EntryKind.Other, "C:1", "other");
        Assert.Equal(page, Chain.EntryFor(parent, "other"));
        Assert.Equal(page, Chain.EntryFor(page, "other"));

        // A file keeps its folder's node and is named by its key, whether opened from the files row or its small group.
        var file = Chain.EntryFor(parent, "f:a.bin");
        Assert.Equal(new ChainEntry(EntryKind.File, "C:1", "f:a.bin"), file);
        Assert.Equal("a.bin", Chain.FileName(file));
        Assert.Equal(file, Chain.EntryFor(new ChainEntry(EntryKind.Small, "C:1", "small"), "f:a.bin"));
    }

    [Fact]
    public void Close_at_drops_the_row_and_everything_below()
    {
        var chain = new[] { Used, Dir("C:1"), Dir("C:5") };
        Assert.Equal([Used], Chain.CloseAt(chain, 1));
        Assert.Empty(Chain.CloseAt(chain, 0));
        Assert.Equal([Used, Dir("C:1")], Chain.CloseAt(chain, 2));
    }

    [Fact]
    public void Prune_missing_cuts_at_the_first_requested_but_absent_node()
    {
        var chain = new[] { Used, Dir("C:1"), Dir("C:5") };
        var requested = new HashSet<string> { "C:0", "C:1", "C:5" };
        Assert.Equal([Used], Chain.PruneMissing(chain, requested, new HashSet<string> { "C:0", "C:5" }));
        Assert.Equal(chain, Chain.PruneMissing(chain, requested, requested));
        // A node that was not requested this poll is not judged.
        Assert.Equal(chain, Chain.PruneMissing(chain, new HashSet<string> { "C:0" }, new HashSet<string> { "C:0" }));
        Assert.Empty(Chain.PruneMissing(chain, requested, new HashSet<string>()));
    }
}
