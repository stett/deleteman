using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DeleteMan.Engine;

/// <summary>
/// An open directory handle (from <see cref="WinFs.OpenDirForEnum"/>), closed on dispose. Used both to enumerate the
/// directory and as the root for relative opens of its children. A kernel handle is just an object reference, so it
/// may be moved and shared between threads.
/// </summary>
public sealed class DirectoryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Wraps a raw handle the caller owns from now on.</summary>
    internal DirectoryHandle(HANDLE raw) : base(ownsHandle: true) => SetHandle(raw);

    /// <summary>The raw handle for P/Invoke calls. Valid until the wrapper is disposed.</summary>
    internal HANDLE Raw => (HANDLE)handle;

    /// <inheritdoc/>
    protected override bool ReleaseHandle() => PInvoke.CloseHandle((HANDLE)handle);
}
