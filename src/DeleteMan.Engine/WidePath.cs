namespace DeleteMan.Engine;

/// <summary>
/// A <c>\\?\</c>-prefixed UTF-16 path. Every path handed to the OS goes through this type, so paths longer than
/// MAX_PATH work and the OS performs no normalisation. Immutable; equality is ordinal on the prefixed string.
/// </summary>
/// <remarks>
/// .NET strings are already UTF-16 and NUL-terminated when pinned, so the prefixed string is passed to the OS as is
/// (the Rust build kept an explicit NUL-terminated <c>Vec&lt;u16&gt;</c> for the same purpose).
/// </remarks>
public sealed class WidePath : IEquatable<WidePath>
{
    private const string Prefix = @"\\?\";
    private const char Backslash = '\\';

    private WidePath(string wide) => Wide = wide;

    /// <summary>The full path including the <c>\\?\</c> prefix (no trailing NUL).</summary>
    public string Wide { get; }

    /// <summary>Human-readable form without the <c>\\?\</c> prefix.</summary>
    public string Display => Wide.StartsWith(Prefix, StringComparison.Ordinal) ? Wide[Prefix.Length..] : Wide;

    /// <summary>True for <c>X:\</c>.</summary>
    public bool IsVolumeRoot
    {
        get
        {
            var w = Display;
            return w.Length == 3 && w[1] == ':' && w[2] == Backslash;
        }
    }

    /// <summary>
    /// Builds a path from components, e.g. <c>["C:", "Users", "x"]</c> → <c>\\?\C:\Users\x</c>. A lone bare drive
    /// component gets a trailing backslash so <c>["C:"]</c> → <c>\\?\C:\</c> (a valid volume root).
    /// </summary>
    public static WidePath FromComponents(params string[] parts)
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append(Prefix);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0 && sb[^1] != Backslash)
            {
                sb.Append(Backslash);
            }
            sb.Append(parts[i]);
        }
        // Only a bare "X:" needs the backslash to name the volume root; a lone component that is already a full
        // directory path (a scan rooted below the drive root) is kept exactly as given.
        if (parts.Length == 1 && IsBareDrive(parts[0]))
        {
            sb.Append(Backslash);
        }
        return new WidePath(sb.ToString());
    }

    /// <summary>
    /// Wraps a string the caller has already built as a complete <c>\\?\</c> path with
    /// <see cref="FromComponents"/>' rules (the scanner writes its paths in one go instead of joining components).
    /// </summary>
    internal static WidePath FromWide(string wide) => new(wide);

    /// <summary>True for a bare drive component such as <c>C:</c>, which needs a backslash to name the volume root.</summary>
    internal static bool IsBareDriveName(string part) => IsBareDrive(part);

    /// <summary>From a display string such as <c>C:\Users\x</c> or <c>\\?\C:\Users\x</c>.</summary>
    public static WidePath FromDisplay(string s)
    {
        var full = s.StartsWith(Prefix, StringComparison.Ordinal) ? s : Prefix + s;
        // Keep the trailing backslash only for volume roots ("C:\").
        var end = full.Length;
        while (end > Prefix.Length + 3 && full[end - 1] == Backslash)
        {
            end--;
        }
        return new WidePath(end == full.Length ? full : full[..end]);
    }

    /// <summary>Appends one component.</summary>
    public WidePath Join(string name) => Join(name.AsSpan());

    /// <summary>Appends one component given as a span (e.g. straight from an enumeration buffer).</summary>
    public WidePath Join(ReadOnlySpan<char> name) =>
        new(Wide[^1] == Backslash ? string.Concat(Wide, name) : string.Concat(Wide, @"\", name));

    /// <inheritdoc/>
    public bool Equals(WidePath? other) => other is not null && string.Equals(Wide, other.Wide, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as WidePath);

    /// <inheritdoc/>
    public override int GetHashCode() => string.GetHashCode(Wide, StringComparison.Ordinal);

    /// <summary>The display form (no prefix).</summary>
    public override string ToString() => Display;

    private static bool IsBareDrive(string part) => part.Length == 2 && part[1] == ':';
}
