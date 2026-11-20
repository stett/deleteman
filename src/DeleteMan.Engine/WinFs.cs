using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Wdk.Foundation;
using Windows.Wdk.Storage.FileSystem;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.IO;

namespace DeleteMan.Engine;

/// <summary>Receives one directory entry from <see cref="WinFs.ListDir"/>: name, attributes and size in bytes.</summary>
public delegate void DirEntryVisitor(ReadOnlySpan<char> name, uint attributes, ulong size);

/// <summary>
/// Receives one directory entry from <see cref="WinFs.EnumerateDirHandle"/>. Return <see langword="false"/> to stop
/// the enumeration early.
/// </summary>
public delegate bool DirEntryPredicate(ReadOnlySpan<char> name, uint attributes, ulong size);

/// <summary>
/// Thin wrappers over the Win32 / NT file APIs used by the scanner and the deleter.
/// </summary>
/// <remarks>
/// Every path handed to the OS is a <c>\\?\</c>-prefixed UTF-16 string, so paths longer than MAX_PATH work and the
/// OS performs no normalisation. Nothing in this class follows reparse points: every open passes
/// <c>FILE_OPEN_REPARSE_POINT</c> / <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, so junctions and symlinks are always handled
/// as the link entry itself, never as their target. Failures throw <see cref="Win32Exception"/> carrying the Win32
/// error code (NTSTATUS values are mapped through <c>RtlNtStatusToDosError</c>).
/// </remarks>
public static unsafe class WinFs
{
    /// <summary>
    /// Attribute bits that mark a file whose content is not actually on the local disk (OneDrive / cloud
    /// placeholders, HSM stubs). Such files are counted as 0 bytes.
    /// </summary>
    public const uint PlaceholderMask =
        (uint)(FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_OFFLINE
            | FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
            | FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_RECALL_ON_OPEN);

    /// <summary>Size in <see cref="ulong"/>s of the 64 KiB enumeration buffer used with <see cref="EnumerateDirHandle"/>.</summary>
    public const int DirBufferWords = 8192;

    private const FILE_SHARE_MODE ShareAll =
        FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE;

    private const FILE_FLAGS_AND_ATTRIBUTES OpenDirFlags =
        FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT;

    private const int MaxRetries = 10;

    /// <summary>True if the directory attribute is set.</summary>
    public static bool IsDir(uint attrs) => (attrs & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY) != 0;

    /// <summary>True if the entry is a reparse point (junction, symlink, cloud placeholder...).</summary>
    public static bool IsReparse(uint attrs) => (attrs & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

    /// <summary>True if the file's content is not on the local disk (see <see cref="PlaceholderMask"/>).</summary>
    public static bool IsPlaceholder(uint attrs) => (attrs & PlaceholderMask) != 0;

    /// <summary>Reads the attributes of a path without following reparse points; <see langword="null"/> if it doesn't exist.</summary>
    public static uint? AttributesOf(WidePath path)
    {
        var a = PInvoke.GetFileAttributes(path.Wide);
        return a == PInvoke.INVALID_FILE_ATTRIBUTES ? null : a;
    }

    /// <summary>
    /// Lists one directory with <c>FindFirstFileExW</c> (basic info level, large fetch) and calls
    /// <paramref name="visit"/> for every entry except <c>.</c> and <c>..</c>. Does not recurse.
    /// </summary>
    /// <exception cref="Win32Exception">The directory cannot be listed.</exception>
    public static void ListDir(WidePath dir, DirEntryVisitor visit)
    {
        var error = TryListDir(dir, visit);
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    /// <summary>Like <see cref="ListDir"/>, but returns the Win32 error code (0 on success) instead of throwing.</summary>
    internal static int TryListDir(WidePath dir, DirEntryVisitor visit)
    {
        // "<dir>\*", null-terminated, in a pooled buffer: the scan lists hundreds of thousands of directories, and
        // a fresh pattern string for each was a measurable share of what it allocated. The OS copies the pattern,
        // so the buffer goes back to the pool as soon as the handle is open.
        var wide = dir.Wide;
        var separator = wide.Length == 0 || wide[^1] != '\\';
        var length = wide.Length + (separator ? 1 : 0) + 2;
        var pattern = ArrayPool<char>.Shared.Rent(length);
        WIN32_FIND_DATAW fd;
        HANDLE h;
        try
        {
            wide.CopyTo(0, pattern, 0, wide.Length);
            var end = wide.Length;
            if (separator)
            {
                pattern[end++] = '\\';
            }
            pattern[end++] = '*';
            pattern[end] = '\0';
            fixed (char* p = pattern)
            {
                h = PInvoke.FindFirstFileEx(
                    p,
                    FINDEX_INFO_LEVELS.FindExInfoBasic,
                    &fd,
                    FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                    null,
                    FIND_FIRST_EX_FLAGS.FIND_FIRST_EX_LARGE_FETCH);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(pattern);
        }
        if (h == HANDLE.INVALID_HANDLE_VALUE)
        {
            var e = Marshal.GetLastPInvokeError();
            // A volume root with no entries at all reports "file not found": that's an empty listing.
            return e == (int)WIN32_ERROR.ERROR_FILE_NOT_FOUND ? 0 : e;
        }
        try
        {
            while (true)
            {
                var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)&fd.cFileName);
                if (!IsDot(name))
                {
                    var size = ((ulong)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                    visit(name, fd.dwFileAttributes, size);
                }
                if (!PInvoke.FindNextFile(h, &fd))
                {
                    var e = Marshal.GetLastPInvokeError();
                    return e == (int)WIN32_ERROR.ERROR_NO_MORE_FILES ? 0 : e;
                }
            }
        }
        finally
        {
            PInvoke.FindClose(h);
        }
    }

    /// <summary>Opens a directory for enumeration (and as the root for relative opens of its children).</summary>
    /// <exception cref="Win32Exception">The directory cannot be opened.</exception>
    public static DirectoryHandle OpenDirForEnum(WidePath path)
    {
        var error = TryOpenDirForEnum(path, out var handle);
        return error == 0 ? handle! : throw new Win32Exception(error);
    }

    /// <summary>Like <see cref="OpenDirForEnum"/>, but returns the Win32 error code (0 on success) instead of throwing.</summary>
    internal static int TryOpenDirForEnum(WidePath path, out DirectoryHandle? handle)
    {
        HANDLE h;
        fixed (char* p = path.Wide)
        {
            h = PInvoke.CreateFile(
                p,
                (uint)(FILE_ACCESS_RIGHTS.FILE_LIST_DIRECTORY | FILE_ACCESS_RIGHTS.SYNCHRONIZE),
                ShareAll,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                OpenDirFlags,
                default);
        }
        if (h == HANDLE.INVALID_HANDLE_VALUE)
        {
            handle = null;
            return Marshal.GetLastPInvokeError();
        }
        handle = new DirectoryHandle(h);
        return 0;
    }

    /// <summary>
    /// Enumerates an open directory handle in 64 KiB batches via <c>FileFullDirectoryInfo</c>, calling
    /// <paramref name="visit"/> for every entry except <c>.</c> and <c>..</c>. Return <see langword="false"/> from
    /// <paramref name="visit"/> to stop early. The buffer is a <see cref="ulong"/> span because the records the kernel
    /// writes are 8-byte aligned.
    /// </summary>
    /// <exception cref="Win32Exception">The enumeration failed.</exception>
    public static void EnumerateDirHandle(DirectoryHandle dir, Span<ulong> buffer, DirEntryPredicate visit)
    {
        var error = TryEnumerateDirHandle(dir, buffer, visit);
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    /// <summary>Like <see cref="EnumerateDirHandle"/>, but returns the Win32 error code (0 on success) instead of throwing.</summary>
    internal static int TryEnumerateDirHandle(DirectoryHandle dir, Span<ulong> buffer, DirEntryPredicate visit)
    {
        var infoClass = FILE_INFO_BY_HANDLE_CLASS.FileFullDirectoryRestartInfo;
        var bytes = (uint)(buffer.Length * sizeof(ulong));
        fixed (ulong* buf = buffer)
        {
            while (true)
            {
                if (!PInvoke.GetFileInformationByHandleEx(dir.Raw, infoClass, buf, bytes))
                {
                    var e = Marshal.GetLastPInvokeError();
                    return e == (int)WIN32_ERROR.ERROR_NO_MORE_FILES ? 0 : e;
                }
                infoClass = FILE_INFO_BY_HANDLE_CLASS.FileFullDirectoryInfo;
                var offset = 0u;
                while (true)
                {
                    // The kernel filled the buffer with a chain of FILE_FULL_DIR_INFO records, each 8-byte aligned and
                    // fully inside the buffer; `offset` only ever follows NextEntryOffset.
                    var info = (FILE_FULL_DIR_INFO*)((byte*)buf + offset);
                    var name = new ReadOnlySpan<char>(&info->FileName, (int)(info->FileNameLength / sizeof(char)));
                    if (!IsDot(name) && !visit(name, info->FileAttributes, (ulong)info->EndOfFile))
                    {
                        return 0;
                    }
                    if (info->NextEntryOffset == 0)
                    {
                        break;
                    }
                    offset += info->NextEntryOffset;
                }
            }
        }
    }

    /// <summary>
    /// Deletes one child entry of an open directory. Files and reparse points (junctions, symlinks) are removed as
    /// entries; a real directory must already be empty. Already-missing entries count as success.
    /// </summary>
    /// <param name="parent">The open parent directory.</param>
    /// <param name="name">The entry name, exactly as enumerated.</param>
    /// <param name="isDir">The entry's directory attribute, so a swapped entry is rejected.</param>
    /// <exception cref="Win32Exception">The entry could not be deleted.</exception>
    public static void DeleteChild(DirectoryHandle parent, ReadOnlySpan<char> name, bool isDir)
    {
        var error = TryDeleteChild(parent, name, isDir);
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    /// <summary>Like <see cref="DeleteChild"/>, but returns the Win32 error code (0 on success) instead of throwing.</summary>
    internal static int TryDeleteChild(DirectoryHandle parent, ReadOnlySpan<char> name, bool isDir)
    {
        var attempt = 0;
        while (true)
        {
            var e = OpenRelativeForDelete(parent.Raw, name, isDir, out var h);
            if (e == 0)
            {
                e = SetDeleteDisposition(h);
                PInvoke.CloseHandle(h);
            }
            if (e == 0 || AlreadyGone(e))
            {
                return 0;
            }
            if (attempt < MaxRetries && Retryable(e))
            {
                attempt++;
                Thread.Yield();
                continue;
            }
            return e;
        }
    }

    /// <summary>
    /// Deletes a file or (empty) directory by full path. Used for the directories themselves after their contents
    /// are gone, and for single-file deletions. Already-missing paths count as success.
    /// </summary>
    /// <exception cref="Win32Exception">The path could not be deleted.</exception>
    public static void DeleteByPath(WidePath path)
    {
        var error = TryDeleteByPath(path);
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    /// <summary>Like <see cref="DeleteByPath"/>, but returns the Win32 error code (0 on success) instead of throwing.</summary>
    internal static int TryDeleteByPath(WidePath path)
    {
        var attempt = 0;
        while (true)
        {
            HANDLE h;
            fixed (char* p = path.Wide)
            {
                h = PInvoke.CreateFile(
                    p,
                    (uint)(FILE_ACCESS_RIGHTS.DELETE | FILE_ACCESS_RIGHTS.SYNCHRONIZE),
                    ShareAll,
                    null,
                    FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                    OpenDirFlags,
                    default);
            }
            int e;
            if (h == HANDLE.INVALID_HANDLE_VALUE)
            {
                e = Marshal.GetLastPInvokeError();
            }
            else
            {
                e = SetDeleteDisposition(h);
                PInvoke.CloseHandle(h);
            }
            if (e == 0 || AlreadyGone(e))
            {
                return 0;
            }
            if (attempt < MaxRetries && Retryable(e))
            {
                attempt++;
                Thread.Yield();
                continue;
            }
            return e;
        }
    }

    /// <summary>
    /// Opens <paramref name="name"/> relative to an open parent directory handle with DELETE access, never following
    /// reparse points. <paramref name="isDir"/> must reflect the entry's directory attribute so a swapped entry is
    /// rejected. Returns 0 and the handle (which the caller closes), or the Win32 error code.
    /// </summary>
    private static int OpenRelativeForDelete(HANDLE parent, ReadOnlySpan<char> name, bool isDir, out HANDLE handle)
    {
        var kind = isDir ? NTCREATEFILE_CREATE_OPTIONS.FILE_DIRECTORY_FILE : NTCREATEFILE_CREATE_OPTIONS.FILE_NON_DIRECTORY_FILE;
        var options = NTCREATEFILE_CREATE_OPTIONS.FILE_OPEN_REPARSE_POINT
            | NTCREATEFILE_CREATE_OPTIONS.FILE_OPEN_FOR_BACKUP_INTENT
            | NTCREATEFILE_CREATE_OPTIONS.FILE_SYNCHRONOUS_IO_NONALERT
            | kind;
        var attributes = OBJECT_ATTRIBUTE_FLAGS.OBJ_DONT_REPARSE;
        fixed (char* n = name)
        {
            var byteLen = (ushort)(name.Length * sizeof(char));
            var us = new UNICODE_STRING { Length = byteLen, MaximumLength = byteLen, Buffer = n };
            while (true)
            {
                var oa = new OBJECT_ATTRIBUTES
                {
                    Length = (uint)sizeof(OBJECT_ATTRIBUTES),
                    RootDirectory = parent,
                    ObjectName = &us,
                    Attributes = attributes,
                };
                IO_STATUS_BLOCK iosb;
                HANDLE h;
                var status = Windows.Wdk.PInvoke.NtOpenFile(
                    &h,
                    (uint)(FILE_ACCESS_RIGHTS.DELETE | FILE_ACCESS_RIGHTS.SYNCHRONIZE),
                    &oa,
                    &iosb,
                    (uint)ShareAll,
                    (uint)options);
                if (status.Value >= 0)
                {
                    handle = h;
                    return 0;
                }
                // Very old kernels reject OBJ_DONT_REPARSE; FILE_OPEN_REPARSE_POINT alone is still safe there.
                if (status == NTSTATUS.STATUS_INVALID_PARAMETER && attributes != 0)
                {
                    attributes = 0;
                    continue;
                }
                handle = default;
                return Win32CodeOf(status);
            }
        }
    }

    /// <summary>
    /// Marks an open handle for deletion. Tries POSIX semantics first (name disappears immediately even if other
    /// handles are open, read-only attribute ignored); falls back to the classic two-step on file systems that don't
    /// support it (FAT32, old network shares). Returns 0 or the Win32 error code.
    /// </summary>
    private static int SetDeleteDisposition(HANDLE h)
    {
        IO_STATUS_BLOCK iosb;
        var info = new FILE_DISPOSITION_INFORMATION_EX
        {
            Flags = FILE_DISPOSITION_INFORMATION_EX_FLAGS.FILE_DISPOSITION_DELETE
                | FILE_DISPOSITION_INFORMATION_EX_FLAGS.FILE_DISPOSITION_POSIX_SEMANTICS
                | FILE_DISPOSITION_INFORMATION_EX_FLAGS.FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE,
        };
        var status = Windows.Wdk.PInvoke.NtSetInformationFile(
            h, &iosb, &info, (uint)sizeof(FILE_DISPOSITION_INFORMATION_EX), FILE_INFORMATION_CLASS.FileDispositionInformationEx);
        if (status.Value >= 0)
        {
            return 0;
        }
        if (status != NTSTATUS.STATUS_INVALID_PARAMETER
            && status != NTSTATUS.STATUS_NOT_SUPPORTED
            && status != NTSTATUS.STATUS_INVALID_INFO_CLASS)
        {
            return Win32CodeOf(status);
        }

        // Legacy path: clear read-only (and every other attribute; the entry is going away), then delete.
        var basic = new FILE_BASIC_INFORMATION { FileAttributes = (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL };
        _ = Windows.Wdk.PInvoke.NtSetInformationFile(
            h, &iosb, &basic, (uint)sizeof(FILE_BASIC_INFORMATION), FILE_INFORMATION_CLASS.FileBasicInformation);
        var disposition = new FILE_DISPOSITION_INFORMATION { DeleteFile = true };
        status = Windows.Wdk.PInvoke.NtSetInformationFile(
            h, &iosb, &disposition, (uint)sizeof(FILE_DISPOSITION_INFORMATION), FILE_INFORMATION_CLASS.FileDispositionInformation);
        return status.Value >= 0 ? 0 : Win32CodeOf(status);
    }

    /// <summary>Converts an NTSTATUS to the Win32 error code the UI shows (e.g. 32 = sharing violation).</summary>
    internal static int Win32CodeOf(NTSTATUS status) => (int)Windows.Win32.PInvoke.RtlNtStatusToDosError(status);

    private static bool Retryable(int e) =>
        e is (int)WIN32_ERROR.ERROR_SHARING_VIOLATION or (int)WIN32_ERROR.ERROR_DELETE_PENDING;

    private static bool AlreadyGone(int e) =>
        e is (int)WIN32_ERROR.ERROR_FILE_NOT_FOUND or (int)WIN32_ERROR.ERROR_PATH_NOT_FOUND;

    private static bool IsDot(ReadOnlySpan<char> name) =>
        name.Length switch
        {
            1 => name[0] == '.',
            2 => name[0] == '.' && name[1] == '.',
            _ => false,
        };
}
