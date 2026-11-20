using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DeleteMan.App;

/// <summary>
/// Entry point. Delete Man runs as a single instance: a second launch hands its activation to the running process
/// (which brings its window forward) and exits without starting XAML. Follows Microsoft's unpackaged
/// single-instance sample (AppInstance + RedirectActivationToAsync + CoWaitForMultipleObjects).
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "DeleteMan";

    /// <summary>Raised on a worker thread when another launch was redirected to this instance.</summary>
    internal static event EventHandler? ActivatedByAnotherLaunch;

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance())
        {
            return 0;
        }
        Shell.Log.Init(App.DataDir);
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        // Application.Start returns once the last window has closed and the app exited.
        App.ShutdownEngine();
        return 0;
    }

    /// <summary>True when another instance owns the key and this activation was handed to it.</summary>
    private static bool RedirectToRunningInstance()
    {
        AppActivationArguments args = AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += (_, _) => ActivatedByAnotherLaunch?.Invoke(null, EventArgs.Empty);
            return false;
        }

        // Redirect on a worker and pump COM on this STA thread meanwhile (a plain Wait could deadlock the call).
        nint done = CreateEventW(0, bManualReset: 1, bInitialState: 0, 0);
        _ = Task.Run(() =>
        {
            try
            {
                keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            }
            finally
            {
                _ = SetEvent(done);
            }
        });
        unsafe
        {
            nint handle = done;
            _ = CoWaitForMultipleObjects(0, uint.MaxValue, 1, &handle, out _);
        }
        _ = CloseHandle(done);

        // This process was just launched, so it may hand the foreground to the running instance.
        try
        {
            using var process = Process.GetProcessById((int)keyInstance.ProcessId);
            if (process.MainWindowHandle != 0)
            {
                _ = SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch (ArgumentException)
        {
            // The other instance exited meanwhile; nothing to bring forward.
        }
        return true;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, nint lpName);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint hEvent);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("ole32.dll")]
    private static unsafe partial int CoWaitForMultipleObjects(uint dwFlags, uint dwTimeout, uint cHandles, nint* pHandles, out uint lpdwindex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);
}
