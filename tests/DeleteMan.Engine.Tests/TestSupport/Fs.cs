using System.Diagnostics;

namespace DeleteMan.Engine.Tests.TestSupport;

/// <summary>Filesystem helpers shared by the engine tests (ports of the helpers in the Rust test modules).</summary>
public static class Fs
{
    public static void Write(string path, int length, byte fill = 0xAB)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        File.WriteAllBytes(path, bytes);
    }

    public static void SetReadOnly(string path)
    {
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
    }

    /// <summary>
    /// The synthetic tree used by the deleter tests (port of delete.rs make_tree): d0..d3 / s0..s3 with five files
    /// each (the first read-only), plus an empty/inner/most chain. Returns (files, dirs).
    /// </summary>
    public static (int Files, int Dirs) MakeTree(string root)
    {
        var files = 0;
        var dirs = 0;
        for (var a = 0; a < 4; a++)
        {
            for (var b = 0; b < 4; b++)
            {
                var d = Path.Combine(root, $"d{a}", $"s{b}");
                Directory.CreateDirectory(d);
                dirs++;
                for (var f = 0; f < 5; f++)
                {
                    var p = Path.Combine(d, $"f{f}.bin");
                    Write(p, 64 * (f + 1), 1);
                    files++;
                    if (f == 0)
                    {
                        SetReadOnly(p);
                    }
                }
            }
            dirs++; // d{a}
        }
        Directory.CreateDirectory(Path.Combine(root, "empty", "inner", "most"));
        dirs += 3;
        return (files, dirs);
    }

    /// <summary>Creates a directory junction with <c>mklink /J</c> (no privileges required).</summary>
    public static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"mklink failed: {proc.StandardError.ReadToEnd()}{proc.StandardOutput.ReadToEnd()}");
        }
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> elapses.</summary>
    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(20);
        }
        return condition();
    }

    public static bool WaitUntil(Func<bool> condition) => WaitUntil(condition, TimeSpan.FromSeconds(30));
}
