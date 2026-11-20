using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeleteMan.Engine;

/// <summary>
/// Persistent record of deletion jobs (<c>jobs.json</c> in the app's local data directory), so that a deletion
/// interrupted by a crash, a power loss or closing the app resumes on the next launch.
/// </summary>
/// <remarks>
/// The file format is exactly what the Rust build wrote (<c>serde_json::to_string_pretty</c>): snake_case keys,
/// lowercase / snake_case enum names, explicit nulls, two-space indentation, <c>\n</c> line breaks and unescaped
/// non-ASCII text. Either build can therefore resume the other's jobs.
/// </remarks>
public sealed class Journal
{
    private const string FileName = "jobs.json";
    private const uint Version = 1;

    /// <summary>
    /// The generated context, re-created with a relaxed encoder so non-ASCII path characters are written as is, like
    /// serde_json does, instead of as <c>\uXXXX</c> escapes.
    /// </summary>
    private static readonly EngineJsonContext Json = new(new JsonSerializerOptions(EngineJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private readonly Lock _writeLock = new();

    /// <summary>Uses <c>jobs.json</c> in <paramref name="dir"/>, creating the directory if needed.</summary>
    public Journal(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"journal: cannot create {dir}: {e.Message}");
        }
        Path = System.IO.Path.Combine(dir, FileName);
    }

    /// <summary>Full path of <c>jobs.json</c>.</summary>
    public string Path { get; }

    /// <summary>
    /// Loads the journal. A missing file yields an empty list; a transient read error (another instance mid-rename,
    /// antivirus holding the file) is retried so running jobs are never silently forgotten; a corrupt file is kept
    /// aside as <c>jobs.json.corrupt</c> so nothing is lost.
    /// </summary>
    public IReadOnlyList<JobRecord> Load()
    {
        string? text = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                text = File.ReadAllText(Path);
                break;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"journal: read attempt {attempt + 1} failed: {e.Message}");
                Thread.Sleep(100);
            }
        }
        if (text is null)
        {
            Trace.TraceWarning($"journal: giving up reading {Path}; starting with no jobs");
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize(text, Json.JournalFile)?.Jobs ?? throw new JsonException("null journal");
        }
        catch (JsonException e)
        {
            Trace.TraceWarning($"journal: {Path} is corrupt ({e.Message}); keeping a copy");
            try
            {
                File.Move(Path, Path + ".corrupt", overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"journal: cannot keep a copy of {Path}: {moveError.Message}");
            }
            return [];
        }
    }

    /// <summary>Atomically replaces the journal (write to a temp file, then rename over the old one).</summary>
    /// <exception cref="IOException">The journal could not be written.</exception>
    public void Save(IReadOnlyList<JobRecord> jobs)
    {
        lock (_writeLock)
        {
            var text = JsonSerializer.Serialize(new JournalFile { Version = Version, Jobs = jobs }, Json.JournalFile);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, Path, overwrite: true);
        }
    }
}

/// <summary>Top-level shape of <c>jobs.json</c>.</summary>
internal sealed class JournalFile
{
    /// <summary>Format version (always 1 so far).</summary>
    public required uint Version { get; init; }

    /// <summary>Every job the host knew about when the file was written.</summary>
    public required IReadOnlyList<JobRecord> Jobs { get; init; }
}

/// <summary>
/// Source-generated JSON metadata for the journal (Native AOT: no reflection-based serialization). The options
/// reproduce serde_json's pretty output.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(JournalFile))]
internal sealed partial class EngineJsonContext : JsonSerializerContext;
