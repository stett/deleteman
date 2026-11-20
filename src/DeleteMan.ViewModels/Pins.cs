// The pin toggle: "keep the largest item open at every level below this row".

namespace DeleteMan.ViewModels;

/// <summary>What a row's pin button shows.</summary>
public enum PinState
{
    /// <summary>No pin button: the row has no folders to follow (a files row) or is being deleted.</summary>
    None,

    /// <summary>Not pinned.</summary>
    Off,

    /// <summary>Pinned: the largest item is kept open at every level below.</summary>
    On,
}

/// <summary>
/// Pin semantics over a drive's chain. A pinned row keeps the largest item open at every level below it (a folder,
/// or the loose files and then the largest file among them), as far as the scan has got, and switches when another
/// item grows past the one it opened. Rules:
/// <list type="bullet">
/// <item>A drive has one pin at a time: pinning a row unpins any other pinned row of the drive, handing it to the
/// new pin as unpinning does, so the newest pin is the one in charge.</item>
/// <item>Pinning a row takes charge of everything below it: rows the user had opened by hand there are handed to
/// the pin, which replaces them with the largest folders right away.</item>
/// <item>Opening a row by hand anywhere below the pin releases it: the click holds, the button turns off, and the
/// pin never fights the user. Pinning again takes the rows over. So every row below a pin is pin-opened.</item>
/// <item>Closing a row the pin opened (the close button, or collapsing its segment) releases the pin, so it never
/// reopens what the user just closed.</item>
/// <item>Unpinning a row marks it as pin-opened, so a pin set above it later may replace it.</item>
/// </list>
/// The drive row has a pin of its own, one level above the chain (<see cref="ChainEntry.DrivePin"/> on the used
/// row): pinning a collapsed drive starts the scan and opens the root under it, and it keeps the largest folder open
/// at every level below the drive just as a pin on the root row would, but the two toggle independently. The
/// one-pin rule covers both.
/// </summary>
public static class Pins
{
    /// <summary>The drive row's position in pin terms: one level above the chain.</summary>
    public const int DriveIndex = -1;

    /// <summary>No pin on the drive.</summary>
    public const int NoPin = -2;

    /// <summary>Index of the deepest pinned row, <see cref="DriveIndex"/> for the drive's own pin, or <see cref="NoPin"/>.</summary>
    public static int Deepest(IReadOnlyList<ChainEntry> chain)
    {
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Pinned)
            {
                return i;
            }
        }
        return chain.Count > 0 && chain[0].DrivePin ? DriveIndex : NoPin;
    }

    /// <summary>
    /// The pin button of row <paramref name="index"/>. -1 is the drive row's own pin: offered while the drive is
    /// collapsed (pinning starts the scan and opens the root under it), on or paused like any pin once set.
    /// </summary>
    public static PinState StateOf(IReadOnlyList<ChainEntry> chain, int index)
    {
        if (index < 0)
        {
            return chain.Count > 0 && chain[0].DrivePin ? PinState.On : PinState.Off;
        }
        if (index >= chain.Count || !HasFolders(chain, index))
        {
            return PinState.None;
        }
        return chain[index].Pinned ? PinState.On : PinState.Off;
    }

    /// <summary>
    /// True when row <paramref name="index"/> can have folders below it to follow: not a files row, a file row, or
    /// the small group of a files row. A page of leftover folders does.
    /// </summary>
    private static bool HasFolders(IReadOnlyList<ChainEntry> chain, int index) => chain[index].Kind switch
    {
        EntryKind.Files or EntryKind.File => false,
        EntryKind.Small => index == 0 || chain[index - 1].Kind != EntryKind.Files,
        _ => true,
    };

    /// <summary>
    /// Flips the pin of row <paramref name="index"/> (-1: the drive row's own pin on an open drive). A drive has one
    /// pin at a time: pinning unpins any other pin of the drive and hands every row below the new pin to it
    /// (hand-opened ones included), so the pin opens the largest folder at every level at once. Unpinning marks the
    /// row as pin-opened.
    /// </summary>
    public static IReadOnlyList<ChainEntry> Toggle(IReadOnlyList<ChainEntry> chain, int index)
    {
        if (index < 0)
        {
            return ToggleDrive(chain);
        }
        if (index >= chain.Count || !HasFolders(chain, index))
        {
            return chain;
        }
        if (chain[index].Pinned)
        {
            var unpinned = chain.ToList();
            unpinned[index] = unpinned[index] with { Pinned = false, Auto = true };
            return unpinned;
        }
        var list = Released(chain).ToList();
        list[index] = list[index] with { Pinned = true };
        for (var i = index + 1; i < list.Count; i++)
        {
            list[i] = list[i] with { Auto = true };
        }
        return list;
    }

    /// <summary>
    /// The drive row's pin while the drive is open (a collapsed drive is the controller's: it starts the scan and
    /// opens the root under the pin). Pinning hands every row, the root included, to the drive.
    /// </summary>
    private static IReadOnlyList<ChainEntry> ToggleDrive(IReadOnlyList<ChainEntry> chain)
    {
        if (chain.Count == 0)
        {
            return chain;
        }
        if (chain[0].DrivePin)
        {
            var off = chain.ToList();
            off[0] = off[0] with { DrivePin = false };
            return off;
        }
        var list = Released(chain).Select(e => e with { Auto = true }).ToList();
        list[0] = list[0] with { DrivePin = true };
        return list;
    }

    /// <summary>
    /// The user clicked segment <paramref name="entry"/> on row <paramref name="index"/>: opens it as a row of
    /// their own, or collapses it when it is the expanded one. Either way the drive's pin is released when the click
    /// lands below it, or collapses a row it opened: the pin never undoes or fights a click.
    /// </summary>
    public static IReadOnlyList<ChainEntry> Expand(IReadOnlyList<ChainEntry> chain, int index, ChainEntry entry)
    {
        var current = index + 1 < chain.Count ? chain[index + 1] : null;
        if (current is not null && current.SegKey == entry.SegKey)
        {
            var head = chain.Take(index + 1).ToList();
            return current.Auto ? Released(head) : head;
        }
        return Released(Chain.ExpandAt(chain, index, entry with { Pinned = false, Auto = false, DrivePin = false }));
    }

    /// <summary>Close on row <paramref name="index"/>: closes it and everything below; releases the pins above it when a pin opened it.</summary>
    public static IReadOnlyList<ChainEntry> Close(IReadOnlyList<ChainEntry> chain, int index)
    {
        var head = Chain.CloseAt(chain, index);
        return index >= 0 && index < chain.Count && chain[index].Auto ? Released(head) : head;
    }

    /// <summary>Every pin off, the drive's included; the rows the pins held stay, as pin-opened.</summary>
    private static IReadOnlyList<ChainEntry> Released(IReadOnlyList<ChainEntry> chain) =>
        chain.Select((e, i) =>
        {
            if (e.Pinned)
            {
                e = e with { Pinned = false, Auto = true };
            }
            return i == 0 && e.DrivePin ? e with { DrivePin = false } : e;
        }).ToList();

    /// <summary>
    /// The segment a pin opens on <paramref name="row"/>: the widest item that can be followed (a readable folder,
    /// the "(files)" group or a single loose file; on a files row, the largest file), or the row's leftover group
    /// ("(other N)" / "(small N)") when everything is folded into it, so the pin goes on through the page of
    /// folders it opens; null when nothing is known yet (no data, nothing with bytes) or the row is a file.
    /// Unreadable folders are never followed, and a page of leftover folders whose folders have not been listed
    /// yet waits for them rather than opening the next page.
    /// </summary>
    public static string? LargestItem(RowView row)
    {
        if (row.Kind is RowKind.Drive or RowKind.File)
        {
            return null;
        }
        SegView? best = null;
        foreach (var s in row.Segs)
        {
            if (s.Expandable && s.Width > 0 && IsFollowable(s.Key) && (best is null || s.Width > best.Width))
            {
                best = s;
            }
        }
        if (best is not null)
        {
            return best.Key;
        }
        if (row.Kind == RowKind.Other)
        {
            return null;
        }
        foreach (var s in row.Segs)
        {
            if (s.Key is "small" or "other" && s.Expandable && s.Width > 0)
            {
                return s.Key;
            }
        }
        return null;
    }

    /// <summary>Folders, "(files)" and single files can be followed; the structural leftovers cannot ("small" / "other" are the fallback).</summary>
    private static bool IsFollowable(string key) => key is not ("other" or "small" or "unscanned" or "drive");

    /// <summary>
    /// Lets every pin in charge do its work: at each level below it, opens the largest folder (replacing a row the
    /// pin opened earlier when another folder has grown past it). Goes as deep as the data in <paramref name="m"/>
    /// allows; a level whose node has not been fetched yet is picked up by the next poll. True when a chain changed.
    /// </summary>
    public static bool Apply(Model m)
    {
        ApplyAndBuild(m, out var changed);
        return changed;
    }

    /// <summary>
    /// <see cref="Apply"/>, returning the rows of the final state so the caller can render them without building
    /// them again. <paramref name="changed"/> is true when a chain changed.
    /// </summary>
    public static IReadOnlyList<RowView> ApplyAndBuild(Model m, out bool changed)
    {
        changed = false;
        // Each pass opens at most one level per drive, then the rows are rebuilt so the new row's segments are seen.
        // Within one call the data is fixed, so passes only ever go deeper; the cap is a guard, not a limit met.
        for (var pass = 0; pass < 256; pass++)
        {
            var rows = Rows.Build(m);
            var byKey = rows.ToDictionary(r => r.Key);
            var stepped = false;
            foreach (var (letter, chain) in m.Chains.ToList())
            {
                var next = Step(chain, i => byKey.GetValueOrDefault(Chain.RowPosKey(letter, i)));
                if (!ReferenceEquals(next, chain))
                {
                    m.Chains[letter] = next;
                    stepped = true;
                }
            }
            if (!stepped)
            {
                return rows;
            }
            changed = true;
        }
        return Rows.Build(m);
    }

    private static IReadOnlyList<ChainEntry> Step(IReadOnlyList<ChainEntry> chain, Func<int, RowView?> rowAt)
    {
        if (Deepest(chain) == NoPin)
        {
            return chain;
        }
        // The drive's own pin starts choosing at the root row, the first row with folders to pick from.
        for (var j = Math.Max(Deepest(chain), 0); j < chain.Count; j++)
        {
            var row = rowAt(j);
            var target = row is null ? null : LargestItem(row);
            if (target is null)
            {
                return chain;
            }
            if (j + 1 < chain.Count && chain[j + 1].SegKey == target)
            {
                continue;
            }
            var head = chain.Take(j + 1).ToList();
            head.Add(Chain.EntryFor(chain[j], target) with { Auto = true });
            return head;
        }
        return chain;
    }
}
