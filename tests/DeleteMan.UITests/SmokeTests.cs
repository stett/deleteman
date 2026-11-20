using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace DeleteMan.UITests;

/// <summary>
/// Launches the real app and drives it through UI Automation. Run with
/// <c>dotnet test --project tests/DeleteMan.UITests/DeleteMan.UITests.csproj -- --explicit only</c>
/// after building src/DeleteMan.App (or point DELETEMAN_EXE at an exe).
/// </summary>
public sealed class SmokeTests
{
    /// <summary>
    /// The app starts, shows its version badge, and opening drive C: produces the used-space row.
    /// </summary>
    [Fact(Explicit = true)]
    public void LaunchOpenDriveAndClose()
    {
        string exe = LocateExe();
        using var app = Application.Launch(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)! });
        using var automation = new UIA3Automation();
        try
        {
            Window window = Retry.WhileNull(
                () => app.GetAllTopLevelWindows(automation).FirstOrDefault(w => w.Title == "Delete Man"),
                TimeSpan.FromSeconds(20)).Result ?? throw new TimeoutException("no \"Delete Man\" window");

            AutomationElement version = WaitFor(window, "version", TimeSpan.FromSeconds(10));
            Assert.StartsWith("v", version.Name, StringComparison.Ordinal);

            AutomationElement driveRow = WaitFor(window, "row:C#-1", TimeSpan.FromSeconds(10));
            AutomationElement segment = driveRow.FindFirstDescendant(cf => cf.ByAutomationId("seg:drive"))
                ?? throw new InvalidOperationException("drive C: has no segment");
            segment.AsButton().Invoke();

            // Row keys are letter#index with the drive row at -1, so the used row is C#0 and its child C#1.
            AutomationElement usedRow = WaitFor(window, "row:C#0", TimeSpan.FromSeconds(15));
            AutomationElement folder = Retry.WhileNull(
                () => usedRow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
                    .FirstOrDefault(e => e.AutomationId.StartsWith("seg:C:", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(15)).Result ?? throw new TimeoutException("the used row shows no folder segment");
            folder.AsButton().Invoke();
            AutomationElement folderRow = WaitFor(window, "row:C#1", TimeSpan.FromSeconds(15));

            // Pin the folder row: the pin opens its largest sub-folder by itself (and keeps going as the scan allows).
            AutomationElement pin = folderRow.FindFirstDescendant(cf => cf.ByAutomationId("ctl:pin"))
                ?? throw new InvalidOperationException("the folder row has no pin button");
            Assert.Equal("Pin", pin.Name);
            pin.AsButton().Invoke();
            WaitFor(window, "row:C#2", TimeSpan.FromSeconds(20));
            Assert.Equal("Unpin (pinned)", Retry.WhileFalse(
                () => (folderRow.FindFirstDescendant(cf => cf.ByAutomationId("ctl:pin"))?.Name ?? "") == "Unpin (pinned)",
                TimeSpan.FromSeconds(5)).Success ? "Unpin (pinned)" : folderRow.FindFirstDescendant(cf => cf.ByAutomationId("ctl:pin"))?.Name);

            window.Close();
            if (!WaitForExit(app, TimeSpan.FromSeconds(5)))
            {
                app.Kill();
            }
        }
        finally
        {
            if (!app.HasExited)
            {
                app.Kill();
            }
        }
    }

    private static bool WaitForExit(Application app, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!app.HasExited && sw.Elapsed < timeout)
        {
            Thread.Sleep(100);
        }
        return app.HasExited;
    }

    private static AutomationElement WaitFor(Window window, string automationId, TimeSpan timeout) =>
        Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), timeout).Result
        ?? throw new TimeoutException($"element '{automationId}' did not appear within {timeout.TotalSeconds} s");

    /// <summary>DELETEMAN_EXE, or the Debug build output next to the repository's tests folder.</summary>
    private static string LocateExe()
    {
        if (Environment.GetEnvironmentVariable("DELETEMAN_EXE") is { Length: > 0 } fromEnv)
        {
            return File.Exists(fromEnv) ? fromEnv : throw new FileNotFoundException("DELETEMAN_EXE does not exist", fromEnv);
        }
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string appBin = Path.Combine(dir.FullName, "src", "DeleteMan.App", "bin");
            if (!Directory.Exists(appBin))
            {
                continue;
            }
            // Platform builds land in bin\x64\Debug, plain ones in bin\Debug; take the newest.
            string? exe = Directory.EnumerateFiles(appBin, "deleteman.exe", SearchOption.AllDirectories)
                .Where(p => p.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !p.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (exe is not null)
            {
                return exe;
            }
        }
        throw new FileNotFoundException("deleteman.exe not found; build src\\DeleteMan.App (Debug) or set DELETEMAN_EXE");
    }
}
