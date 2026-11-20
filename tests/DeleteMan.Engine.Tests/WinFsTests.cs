using System.ComponentModel;
using DeleteMan.Engine.Tests.TestSupport;

namespace DeleteMan.Engine.Tests;

/// <summary>Behaviour of the thin Win32/NT wrappers (winfs.rs) on a real NTFS temp directory.</summary>
public class WinFsTests
{
    private static List<(string Name, uint Attrs, ulong Size)> Collect(WidePath dir)
    {
        var entries = new List<(string, uint, ulong)>();
        WinFs.ListDir(dir, (name, attrs, size) => entries.Add((name.ToString(), attrs, size)));
        return entries;
    }

    [Fact]
    public void Attribute_helpers_read_the_right_bits()
    {
        Assert.True(WinFs.IsDir(0x10));
        Assert.False(WinFs.IsDir(0x20));
        Assert.True(WinFs.IsReparse(0x400));
        Assert.True(WinFs.IsPlaceholder(0x1000));       // FILE_ATTRIBUTE_OFFLINE
        Assert.True(WinFs.IsPlaceholder(0x400000));     // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
        Assert.True(WinFs.IsPlaceholder(0x40000));      // FILE_ATTRIBUTE_RECALL_ON_OPEN
        Assert.False(WinFs.IsPlaceholder(0x20 | 0x1));
    }

    [Fact]
    public void List_dir_reports_names_attributes_and_sizes_without_dot_entries()
    {
        using var tmp = new TempDir();
        Fs.Write(tmp.Sub("a.bin"), 10);
        Fs.Write(tmp.Sub("b.bin"), 2000);
        Fs.SetReadOnly(tmp.Sub("b.bin"));
        Directory.CreateDirectory(tmp.Sub("sub"));

        var entries = Collect(WidePath.FromDisplay(tmp.Path)).OrderBy(e => e.Name).ToList();
        Assert.Equal(["a.bin", "b.bin", "sub"], entries.Select(e => e.Name));
        Assert.Equal(10UL, entries[0].Size);
        Assert.Equal(2000UL, entries[1].Size);
        Assert.True((entries[1].Attrs & 0x1) != 0, "read-only bit");
        Assert.True(WinFs.IsDir(entries[2].Attrs));
        Assert.False(WinFs.IsDir(entries[0].Attrs));
    }

    [Fact]
    public void List_dir_of_an_empty_directory_yields_nothing_and_of_a_missing_one_throws()
    {
        using var tmp = new TempDir();
        Assert.Empty(Collect(WidePath.FromDisplay(tmp.Path)));
        var ex = Assert.Throws<Win32Exception>(() => Collect(WidePath.FromDisplay(tmp.Sub("nope"))));
        Assert.Contains(ex.NativeErrorCode, new[] { 2, 3 }); // FILE_NOT_FOUND / PATH_NOT_FOUND
    }

    [Fact]
    public void Attributes_of_missing_path_is_null()
    {
        using var tmp = new TempDir();
        Assert.Null(WinFs.AttributesOf(WidePath.FromDisplay(tmp.Sub("missing"))));
        var attrs = WinFs.AttributesOf(WidePath.FromDisplay(tmp.Path));
        Assert.NotNull(attrs);
        Assert.True(WinFs.IsDir(attrs.Value));
    }

    [Fact]
    public void Enumerate_dir_handle_matches_list_dir_and_can_stop_early()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 300; i++)
        {
            Fs.Write(tmp.Sub($"file-{i:D4}.bin"), i + 1);
        }
        Directory.CreateDirectory(tmp.Sub("dir"));

        var listed = Collect(WidePath.FromDisplay(tmp.Path)).OrderBy(e => e.Name).ToList();
        var enumerated = new List<(string Name, uint Attrs, ulong Size)>();
        using (var handle = WinFs.OpenDirForEnum(WidePath.FromDisplay(tmp.Path)))
        {
            var buffer = new ulong[WinFs.DirBufferWords];
            WinFs.EnumerateDirHandle(handle, buffer, (name, attrs, size) =>
            {
                enumerated.Add((name.ToString(), attrs, size));
                return true;
            });
        }
        enumerated.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        Assert.Equal(listed.Select(e => (e.Name, e.Size)), enumerated.Select(e => (e.Name, e.Size)));
        Assert.Equal(301, enumerated.Count);

        var seen = 0;
        using (var handle = WinFs.OpenDirForEnum(WidePath.FromDisplay(tmp.Path)))
        {
            var buffer = new ulong[WinFs.DirBufferWords];
            WinFs.EnumerateDirHandle(handle, buffer, (_, _, _) => ++seen < 5);
        }
        Assert.Equal(5, seen);
    }

    [Fact]
    public void Delete_by_path_removes_read_only_files_and_treats_missing_as_success()
    {
        using var tmp = new TempDir();
        var file = tmp.Sub("ro.bin");
        Fs.Write(file, 5);
        Fs.SetReadOnly(file);
        WinFs.DeleteByPath(WidePath.FromDisplay(file));
        Assert.False(File.Exists(file));
        WinFs.DeleteByPath(WidePath.FromDisplay(file)); // already gone: no exception
    }

    [Fact]
    public void Delete_child_removes_entries_relative_to_the_parent_handle()
    {
        using var tmp = new TempDir();
        Fs.Write(tmp.Sub("a.bin"), 1);
        Directory.CreateDirectory(tmp.Sub("emptydir"));
        using (var handle = WinFs.OpenDirForEnum(WidePath.FromDisplay(tmp.Path)))
        {
            WinFs.DeleteChild(handle, "a.bin".AsSpan(), isDir: false);
            WinFs.DeleteChild(handle, "emptydir".AsSpan(), isDir: true);
            WinFs.DeleteChild(handle, "never-existed".AsSpan(), isDir: false); // missing = success
        }
        Assert.False(File.Exists(tmp.Sub("a.bin")));
        Assert.False(Directory.Exists(tmp.Sub("emptydir")));
    }

    [Fact]
    public void Delete_child_on_a_junction_removes_the_link_and_leaves_the_target()
    {
        using var tmp = new TempDir();
        var target = tmp.Sub("target");
        Directory.CreateDirectory(target);
        Fs.Write(Path.Combine(target, "CANARY.txt"), 9);
        var link = tmp.Sub("link");
        Fs.CreateJunction(link, target);
        Assert.True(File.Exists(Path.Combine(link, "CANARY.txt")));

        var entries = Collect(WidePath.FromDisplay(tmp.Path));
        var linkEntry = entries.Single(e => e.Name == "link");
        Assert.True(WinFs.IsDir(linkEntry.Attrs));
        Assert.True(WinFs.IsReparse(linkEntry.Attrs));

        using (var handle = WinFs.OpenDirForEnum(WidePath.FromDisplay(tmp.Path)))
        {
            WinFs.DeleteChild(handle, "link".AsSpan(), isDir: true);
        }
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "CANARY.txt")));
    }

    [Fact]
    public void Delete_child_reports_sharing_violation_with_win32_code_32()
    {
        using var tmp = new TempDir();
        var held = tmp.Sub("held.bin");
        Fs.Write(held, 3);
        using var stream = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var handle = WinFs.OpenDirForEnum(WidePath.FromDisplay(tmp.Path));
        var ex = Assert.Throws<Win32Exception>(() => WinFs.DeleteChild(handle, "held.bin".AsSpan(), isDir: false));
        Assert.Equal(32, ex.NativeErrorCode);
    }
}
