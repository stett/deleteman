# Delete Man

A Windows desktop app (C# / WinUI 3, .NET 10, Native AOT) with two jobs:

1. **Find what is eating disk space.** Folder sizes come from a breadth-first scan, so the top levels
   appear within seconds and refine while deeper levels finish.
2. **Delete huge, deep folders as fast as NTFS allows.** Deletion runs in parallel through NT-level calls
   with POSIX delete semantics, and is journaled so that a deletion interrupted by a crash or by closing
   the app resumes on the next launch.

## Installing

Both downloads are on the [Releases](https://github.com/stett/deleteman/releases/latest) page. 64-bit
Windows 10 or 11.

- **Installer**, `Delete Man_<version>_x64-setup.exe`: installs to `%LOCALAPPDATA%\Delete Man` for the
  current user (no administrator rights), adds a Start Menu shortcut, installs the Windows App Runtime
  (Windows App SDK 2.6, x64) if it is missing, and puts the install folder on your user PATH so that
  `deleteman` works from any **new** terminal. `/S` installs silently. Uninstall from "Apps & features" or
  with `uninstall.exe` in the install folder.
- **Portable**, `deleteman-<version>-win-x64.zip`: unzip into any folder and run `deleteman.exe`. Nothing
  to install; the runtime files are in the zip.

## How it works

**Scanning** lists every directory at one depth in parallel before going a level deeper, and adds each
directory's bytes to all its ancestors as soon as it is listed, so sizes are live lower bounds that settle
when the scan finishes. Junctions and symbolic links are never followed, and folders the app cannot read
are shown as access denied rather than skipped.

**Deleting** opens each directory once, opens every file relative to its parent handle and marks it for
POSIX delete, so names vanish immediately and read-only files need no extra round trip. Sibling subtrees
are deleted in parallel on a dedicated thread pool, which is where NTFS scales. Every step is idempotent
and the job is journaled, so an interrupted deletion simply runs again on the next launch. Paths are
`\?\`-prefixed, so long paths work.

The rest of the documentation is in [`docs/`](docs/): [what every row, segment and button does](docs/ui.md),
[the scanner and deleter in detail](docs/engine.md), and
[building, testing, publishing and releasing](docs/development.md).
