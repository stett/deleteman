namespace DeleteMan.Engine.Tests.TestSupport;

/// <summary>A unique, empty directory under %TEMP%\dm-tests, removed (best effort) on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                ClearReadOnly(new DirectoryInfo(Path));
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked file from a test that deliberately holds one open; leave it for the OS temp cleaner.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
            {
                info.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }
}
