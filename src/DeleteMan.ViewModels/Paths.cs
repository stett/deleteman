namespace DeleteMan.ViewModels;

public static class Paths
{
    private static readonly string[] SystemRoots =
    [
        @"c:\windows",
        @"c:\program files",
        @"c:\program files (x86)",
        @"c:\programdata",
    ];

    private const string ProfileParent = @"c:\users\";

    public static string JoinPath(string dir, string name) => dir.EndsWith('\\') ? dir + name : $"{dir}\\{name}";

    /// <summary>C:\Windows, C:\Program Files[ (x86)], C:\ProgramData or a user profile (C:\Users\name).</summary>
    public static bool IsSystemRoot(string path)
    {
        var p = path.TrimEnd('\\');
        foreach (var root in SystemRoots)
        {
            if (string.Equals(p, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        if (p.Length > ProfileParent.Length && p.StartsWith(ProfileParent, StringComparison.OrdinalIgnoreCase))
        {
            return !p.AsSpan(ProfileParent.Length).Contains('\\');
        }
        return false;
    }
}
