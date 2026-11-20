using DeleteMan.Engine;
using Microsoft.UI.Xaml;

namespace DeleteMan.App;

/// <summary>Application object: creates the engine and the main window.</summary>
public partial class App : Application
{
    private static EngineHost? _engine;
    private MainWindow? _window;

    /// <summary>
    /// Engine state (journal of running deletions, log) lives where the Tauri build kept it, so jobs carry over
    /// between the two builds.
    /// </summary>
    internal static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "com.deleteman.app");

    /// <summary>Loads App.xaml (theme resources and control styles).</summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Shell.Log.Unhandled("xaml: " + e.Message, e.Exception);
    }

    /// <summary>Disposes the engine after a normal exit (called by <see cref="Program"/> once XAML has stopped).</summary>
    internal static void ShutdownEngine()
    {
        _engine?.Dispose();
        _engine = null;
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _engine = new EngineHost(DataDir);
        _window = new MainWindow(_engine);
        Program.ActivatedByAnotherLaunch += (_, _) => _window.DispatcherQueue.TryEnqueue(_window.BringToFront);
        _window.Activate();
    }
}
