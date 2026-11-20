using System.Reflection;
using System.Runtime.InteropServices;
using DeleteMan.App.Shell;
using DeleteMan.Engine;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DeleteMan.App;

/// <summary>
/// The application window: Mica, custom title bar, size limits, the drive cards and the jobs banner. Behaviour lives
/// in <see cref="AppController"/>; this class wires it up and owns the close prompt.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly EngineHost _engine;
    private readonly AppController _controller;
    private bool _closeConfirmed;
    private bool _closePromptOpen;

    /// <summary>Creates the window for <paramref name="engine"/> and starts polling it.</summary>
    public MainWindow(EngineHost engine)
    {
        _engine = engine;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Preferred minimum sizes are physical pixels; scale the 900×500 DIP minimum by the window's DPI.
            double scale = DpiScale();
            presenter.PreferredMinimumWidth = (int)Math.Round(900 * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(500 * scale);
        }
        ResizeClient(1280, 820);
        VersionBadge.Text = "v" + Version();

        var rows = new RowsPresenter(Cards);
        var jobs = new JobsPresenter(JobsBanner, JobLines);
        _controller = new AppController(engine, rows, jobs, () => Root.XamlRoot);
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => _controller.Stop();
        Root.Loaded += (_, _) => _ = _controller.RunAsync();
    }

    /// <summary>Shows, restores and foregrounds the window (another launch was redirected here).</summary>
    internal void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        AppWindow.Show();
        Activate();
        _ = SetForegroundWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id));
    }

    /// <summary>The product version without the source-revision suffix ("0.2.0+abc" → "0.2.0").</summary>
    private static string Version()
    {
        string? v = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(v))
        {
            return "0.0.0";
        }
        int plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? v[..plus] : v;
    }

    private double DpiScale()
    {
        uint dpi = GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    private void ResizeClient(int width, int height)
    {
        double scale = DpiScale();
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// While a deletion runs, closing asks first; "Close anyway" saves the journal (the deletion resumes on the next
    /// launch) and closes.
    /// </summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || _engine.RequestClose())
        {
            return;
        }
        args.Cancel = true;
        if (_closePromptOpen)
        {
            return;
        }
        _closePromptOpen = true;
        try
        {
            if (await Dialogs.Dialogs.ConfirmCloseAsync(Root.XamlRoot))
            {
                _engine.ForceClose();
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);
}
