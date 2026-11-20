using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DeleteMan.Engine.Recycle;

/// <summary>Moves a file or directory to the Recycle Bin with the shell's <c>IFileOperation</c>.</summary>
internal static class RecycleBin
{
    private static readonly Guid IidFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    /// <summary>
    /// Shown when the shell refuses to recycle. The common case is the shell aborting when it would have to
    /// permanently delete rather than recycle: too big for the bin, or a drive whose Recycle Bin is off or
    /// unavailable. The app asked for a nuke warning, so the shell refuses rather than silently destroying the data.
    /// That refusal is the safe outcome; tell the user to use permanent delete instead.
    /// </summary>
    internal const string WouldNotRecycleMessage =
        "Windows would not move this to the Recycle Bin. It is either too large for the bin or on a drive "
        + "whose Recycle Bin is turned off, so recycling it would permanently delete it. Use permanent "
        + "delete if that is what you want.";

    /// <summary>
    /// Moves <paramref name="path"/> to the Recycle Bin, blocking until the shell is done. The shell's copy engine
    /// needs a single-threaded apartment: on an STA thread this runs inline, otherwise on a dedicated STA thread.
    /// </summary>
    /// <exception cref="IOException">The shell refused or failed; the message is meant for the user.</exception>
    public static void Delete(WidePath path)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            DeleteOnSta(path.Display);
            return;
        }
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                DeleteOnSta(path.Display);
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        })
        {
            Name = "recycle",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static void DeleteOnSta(string displayPath)
    {
        // The shell parses display paths; it does not accept the \\?\ prefix.
        Check(FileOperationInterop.SHCreateItemFromParsingName(displayPath, 0, IidShellItem, out var itemPtr));
        var item = (IShellItem)ComWrappers.GetOrCreateObjectForComInstance(itemPtr, CreateObjectFlags.UniqueInstance);
        Marshal.Release(itemPtr);
        try
        {
            Check(FileOperationInterop.CoCreateInstance(
                FileOperationInterop.ClsidFileOperation, 0, FileOperationInterop.ClsctxAll, IidFileOperation, out var opPtr));
            var op = (IFileOperation)ComWrappers.GetOrCreateObjectForComInstance(opPtr, CreateObjectFlags.UniqueInstance);
            Marshal.Release(opPtr);
            try
            {
                Perform(op, item);
            }
            finally
            {
                ((ComObject)(object)op).FinalRelease();
            }
        }
        finally
        {
            ((ComObject)(object)item).FinalRelease();
        }
    }

    private static void Perform(IFileOperation op, IShellItem item)
    {
        // Never FOFX_RECYCLEONDELETE: it would make the shell fall back to a permanent delete without asking.
        Check(op.SetOperationFlags(
            FileOperationInterop.FofAllowUndo
            | FileOperationInterop.FofNoUi
            | FileOperationInterop.FofWantNukeWarning
            | FileOperationInterop.FofxEarlyFailure));
        var sink = new DeleteProgressSink();
        Check(op.DeleteItem(item, sink));
        var hr = op.PerformOperations();
        var abortedHr = op.GetAnyOperationsAborted(out var aborted);
        if ((abortedHr >= 0 && aborted != 0)
            || hr is FileOperationInterop.EAbort or FileOperationInterop.CopyEngineUserCancelled)
        {
            throw new IOException(WouldNotRecycleMessage);
        }
        Check(hr);
        Check(sink.FailedResult);
    }

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            throw new IOException($"Recycle Bin: {Marshal.GetPInvokeErrorMessage(hr)} (0x{hr:X8})");
        }
    }
}
