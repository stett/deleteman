using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DeleteMan.Engine.Recycle;

// Source-generated (Native AOT safe) COM declarations for the shell's IFileOperation, used to move items to the
// Recycle Bin. Only the vtable order matters to COM; every method is declared, in order, so the slots line up, and
// every method returns its HRESULT ([PreserveSig]) so failures are inspected rather than thrown.

/// <summary>Shell item (shobjidl_core.h). Only passed to <see cref="IFileOperation"/>; its methods are declared for the vtable.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig]
    int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);

    [PreserveSig]
    int GetParent(out IShellItem ppsi);

    [PreserveSig]
    int GetDisplayName(uint sigdnName, out nint ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(IShellItem psi, uint hint, out int piOrder);
}

/// <summary>
/// Progress callbacks of <see cref="IFileOperation"/> (shobjidl_core.h). Shell items arrive as raw pointers: the sink
/// only reads HRESULTs, so it never needs a managed wrapper for them.
/// </summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
internal partial interface IFileOperationProgressSink
{
    [PreserveSig]
    int StartOperations();

    [PreserveSig]
    int FinishOperations(int hrResult);

    [PreserveSig]
    int PreRenameItem(uint dwFlags, nint psiItem, string? pszNewName);

    [PreserveSig]
    int PostRenameItem(uint dwFlags, nint psiItem, string? pszNewName, int hrRename, nint psiNewlyCreated);

    [PreserveSig]
    int PreMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, string? pszNewName);

    [PreserveSig]
    int PostMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, string? pszNewName, int hrMove, nint psiNewlyCreated);

    [PreserveSig]
    int PreCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, string? pszNewName);

    [PreserveSig]
    int PostCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, string? pszNewName, int hrCopy, nint psiNewlyCreated);

    [PreserveSig]
    int PreDeleteItem(uint dwFlags, nint psiItem);

    [PreserveSig]
    int PostDeleteItem(uint dwFlags, nint psiItem, int hrDelete, nint psiNewlyCreated);

    [PreserveSig]
    int PreNewItem(uint dwFlags, nint psiDestinationFolder, string? pszNewName);

    [PreserveSig]
    int PostNewItem(uint dwFlags, nint psiDestinationFolder, string? pszNewName, string? pszTemplateName, uint dwFileAttributes, int hrNew, nint psiNewItem);

    [PreserveSig]
    int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);

    [PreserveSig]
    int ResetTimer();

    [PreserveSig]
    int PauseTimer();

    [PreserveSig]
    int ResumeTimer();
}

/// <summary>The shell's copy engine (shobjidl_core.h), created from <see cref="FileOperationInterop.ClsidFileOperation"/>.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
internal partial interface IFileOperation
{
    [PreserveSig]
    int Advise(IFileOperationProgressSink pfops, out uint pdwCookie);

    [PreserveSig]
    int Unadvise(uint dwCookie);

    [PreserveSig]
    int SetOperationFlags(uint dwOperationFlags);

    [PreserveSig]
    int SetProgressMessage(string pszMessage);

    [PreserveSig]
    int SetProgressDialog(nint popd);

    [PreserveSig]
    int SetProperties(nint pproparray);

    [PreserveSig]
    int SetOwnerWindow(nint hwndOwner);

    [PreserveSig]
    int ApplyPropertiesToItem(IShellItem psiItem);

    [PreserveSig]
    int ApplyPropertiesToItems(nint punkItems);

    [PreserveSig]
    int RenameItem(IShellItem psiItem, string pszNewName, IFileOperationProgressSink? pfopsItem);

    [PreserveSig]
    int RenameItems(nint pUnkItems, string pszNewName);

    [PreserveSig]
    int MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, IFileOperationProgressSink? pfopsItem);

    [PreserveSig]
    int MoveItems(nint punkItems, IShellItem psiDestinationFolder);

    [PreserveSig]
    int CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, string? pszCopyName, IFileOperationProgressSink? pfopsItem);

    [PreserveSig]
    int CopyItems(nint punkItems, IShellItem psiDestinationFolder);

    [PreserveSig]
    int DeleteItem(IShellItem psiItem, IFileOperationProgressSink? pfopsItem);

    [PreserveSig]
    int DeleteItems(nint punkItems);

    [PreserveSig]
    int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, string pszName, string? pszTemplateName, IFileOperationProgressSink? pfopsItem);

    [PreserveSig]
    int PerformOperations();

    [PreserveSig]
    int GetAnyOperationsAborted(out int pfAnyOperationsAborted);
}

/// <summary>
/// Records the result the shell reports for the deleted item, so a failure can be described precisely. All other
/// callbacks are no-ops.
/// </summary>
[GeneratedComClass]
internal sealed partial class DeleteProgressSink : IFileOperationProgressSink
{
    private const int SOk = 0;

    /// <summary>The first failing HRESULT reported for a deleted item, or 0.</summary>
    public int FailedResult { get; private set; }

    public int PostDeleteItem(uint dwFlags, nint psiItem, int hrDelete, nint psiNewlyCreated)
    {
        if (hrDelete < 0 && FailedResult == 0)
        {
            FailedResult = hrDelete;
        }
        return SOk;
    }

    public int StartOperations() => SOk;

    public int FinishOperations(int hrResult) => SOk;

    public int PreRenameItem(uint dwFlags, nint psiItem, string? pszNewName) => SOk;

    public int PostRenameItem(uint dwFlags, nint psiItem, string? pszNewName, int hrRename, nint psiNewlyCreated) => SOk;

    public int PreMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, string? pszNewName) => SOk;

    public int PostMoveItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, string? pszNewName, int hrMove, nint psiNewlyCreated) => SOk;

    public int PreCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, string? pszNewName) => SOk;

    public int PostCopyItem(uint dwFlags, nint psiItem, nint psiDestinationFolder, string? pszNewName, int hrCopy, nint psiNewlyCreated) => SOk;

    public int PreDeleteItem(uint dwFlags, nint psiItem) => SOk;

    public int PreNewItem(uint dwFlags, nint psiDestinationFolder, string? pszNewName) => SOk;

    public int PostNewItem(uint dwFlags, nint psiDestinationFolder, string? pszNewName, string? pszTemplateName, uint dwFileAttributes, int hrNew, nint psiNewItem) => SOk;

    public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => SOk;

    public int ResetTimer() => SOk;

    public int PauseTimer() => SOk;

    public int ResumeTimer() => SOk;
}

/// <summary>Flat entry points and constants for the Recycle Bin path (hand-written: blittable, AOT safe).</summary>
internal static partial class FileOperationInterop
{
    /// <summary>CLSID_FileOperation.</summary>
    public static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    /// <summary>Keep the item undoable, i.e. move it to the Recycle Bin.</summary>
    public const uint FofAllowUndo = 0x0040;

    /// <summary>FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR: no UI of any kind.</summary>
    public const uint FofNoUi = 0x0004 | 0x0010 | 0x0400 | 0x0200;

    /// <summary>
    /// Ask before an item would be permanently deleted instead of recycled. With no UI the shell cannot ask, so it
    /// aborts the operation: the safe outcome.
    /// </summary>
    public const uint FofWantNukeWarning = 0x4000;

    /// <summary>Stop at the first failure instead of carrying on with other items.</summary>
    public const uint FofxEarlyFailure = 0x00100000;

    /// <summary>E_ABORT: the operation was aborted.</summary>
    public const int EAbort = unchecked((int)0x80004004);

    /// <summary>COPYENGINE_E_USER_CANCELLED: the copy engine cancelled the operation.</summary>
    public const int CopyEngineUserCancelled = unchecked((int)0x80270000);

    /// <summary>CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER.</summary>
    public const uint ClsctxAll = 0x17;

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(string pszPath, nint pbc, in Guid riid, out nint ppv);
}
