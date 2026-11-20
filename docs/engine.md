# Engine internals

How `src\DeleteMan.Engine` deletes and scans.

## How deletion works

`src\DeleteMan.Engine\WinFs.cs` and `Deleter.cs`:

- Each directory is opened once (`CreateFileW` with backup semantics, never following reparse points) and
  enumerated in 64 KiB batches with `GetFileInformationByHandleEx(FileFullDirectoryInfo)`.
- Each file is opened **relative to the parent handle** with `NtOpenFile` (no path re-parsing) and marked
  with `FileDispositionInformationEx` using `POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE`, so read-only
  files need no attribute round-trip and names vanish immediately even if a handle is open elsewhere.
  File systems without POSIX delete (FAT32) fall back to the classic two-step.
- Subdirectories are deleted **in parallel** on a dedicated thread pool (`DeletePool.cs`, `2 × cores`,
  8 to 32 threads); a directory is removed as soon as its own contents are gone. Parallelism across sibling
  subtrees is where NTFS scales; deletes within one directory serialise on its index lock.
- Sharing violations are retried briefly, already-missing entries count as success, and every step is
  idempotent, which is what makes resume a matter of running the same job again.
- All paths are `\\?\`-prefixed UTF-16, so paths longer than 260 characters work.

## How scanning works

`src\DeleteMan.Engine\Scanner\DriveScan.cs` keeps one append-only arena of directory nodes per drive (files
are only summed into their parent). The scan is level-synchronous: all directories at depth *d* are listed
in parallel (`FindFirstFileExW` with `FIND_FIRST_EX_LARGE_FETCH`) before depth *d+1* starts, and each
directory's bytes are added to every ancestor as soon as it is listed. Sizes are therefore live lower
bounds until the scan reports done. Scanning pauses while a deletion runs so the deletion owns the disk.

