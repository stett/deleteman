using System.Diagnostics;

namespace DeleteMan.App.Shell;

/// <summary>
/// Minimal diagnostics: <see cref="Trace"/> output and unhandled exceptions go to <c>deleteman.log</c> next to the
/// journal. The file is rewritten on every start so it never grows. It exists so that a blank window or a silently
/// failing action can be explained after the fact (the poll loop and every action trace their failures).
/// </summary>
internal static class Log
{
    /// <summary>Full path of the log file, or null when it could not be created.</summary>
    public static string? Path { get; private set; }

    /// <summary>Starts logging into <paramref name="dataDir"/> and hooks the process-wide unhandled-exception events.</summary>
    public static void Init(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            Path = System.IO.Path.Combine(dataDir, "deleteman.log");
            var writer = new StreamWriter(Path, append: false) { AutoFlush = true };
            Trace.Listeners.Add(new TextWriterTraceListener(writer, "file"));
            Trace.AutoFlush = true;
            Trace.TraceInformation($"Delete Man starting: {Environment.ProcessPath} (pid {Environment.ProcessId})");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to log is no reason not to start.
            Path = null;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Unhandled("process", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Unhandled("task", e.Exception);
    }

    /// <summary>Records an exception nobody handled.</summary>
    public static void Unhandled(string source, Exception? e) => Trace.TraceError($"unhandled ({source}): {e}");
}
