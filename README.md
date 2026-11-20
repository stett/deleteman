# Delete Man

A Windows desktop app (C# / WinUI 3, .NET 10, Native AOT) with two jobs:

1. **Find what is eating disk space.** Folder sizes come from a breadth-first scan, so the top levels
   appear within seconds and refine while deeper levels finish.
3. **Delete huge, deep folders as fast as NTFS allows.** Deletion runs in parallel through NT-level calls
   with POSIX delete semantics, and is journaled so that a deletion interrupted by a crash or by closing the app
   resumes on the next launch.

## Installing

**Installer:** run `Delete Man_<version>_x64-setup.exe` (`scripts\release.ps1` writes it to `artifacts\`).
The installer:

- installs to `%LOCALAPPDATA%\Delete Man` for the current user (no administrator rights needed),
- adds a Start Menu shortcut named **Delete Man**,
- installs the **Windows App Runtime** (Windows App SDK 2.6, x64) silently if it is missing. The installed
  app uses the shared runtime instead of carrying its own copy. If this step fails, setup still finishes,
  warns you, and points you to
  [the Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads), and
- **adds the install folder to your user PATH**, so you can launch it from any terminal by typing
  `deleteman` (the app shows as "Delete Man" in Windows, but the command is one word).

Because PATH is read when a shell starts, open a **new** terminal after installing for `deleteman` to be
found. From an already-open shell you can run it once with its full path,
`"%LOCALAPPDATA%\Delete Man\deleteman.exe"`.

```powershell
# install silently (no prompts)
.\"Delete Man_0.2.0_x64-setup.exe" /S
# then, in a new terminal:
deleteman
```

To uninstall, use "Apps & features" or run `uninstall.exe` in the install folder (`/S` for silent).
Uninstalling removes the PATH entry it added. The Windows App Runtime is shared with other apps and is
left installed. Installing 0.2 over the 0.1 (Tauri) install replaces it in place.

**Portable:** unzip `deleteman-<version>-win-x64.zip` (on the `release/<major>.<minor>` branch, in
`release\`) into any folder and run `deleteman.exe`. Nothing to install: the zip is self-contained, with
the Windows App Runtime files next to the executable.

## Using it

- **Drive rows** show capacity on the right and the used fraction as the filled part of the bar. Hover for
  used/free. Click to scan the drive and open its *used* row.
- **Used row** (`C:\`) is the used space at full width, split into the root folders, `(files)` for files
  directly in the root, and a hatched `unscanned` remainder that shrinks as the scan proceeds (after the
  scan it becomes `other/system`: metadata, protected folders, shadow copies).
- **Folder rows** show the folder's total on the right and its subfolders as segments, largest first.
  Segment names are clipped to the segment; hover to see the full path, size and file count. Subfolders under
  0.25 % of the bar, and any past the 64 largest (all the engine lists for a row), fold into one leftover
  segment at the end of the bar: `(small N)` when every subfolder was listed, `(other N)` when some were not.
  Click it for a page of those folders, largest first, at their own scale; a page's own leftover (its tiny
  folders, and whatever lies beyond the page) opens the next page. Access-denied folders appear as striped red
  slivers.
  Click `(files)` to list the files directly in the folder, largest first; click a file to open it as its own
  row, which carries the Recycle Bin and delete buttons for that one file. When the loose files amount to one
  file (the only one, or the only one big enough to show), the segment carries that file's name instead of
  `(files)` and opens its row directly.
- **Controls** on each row, as two joined pairs: ♻ move to the Recycle Bin with 🗑 delete permanently (folder
  and file rows only), and pin (a double chevron down, "expand all") with close (a chevron up, "contract this").
  Pin and close are on every row, drive rows included, and are greyed out where they cannot act: the pin on a row
  with no folders below it, close on a collapsed drive. The pin keeps the largest item open at every level below
  the row and follows the scan as sizes change: a folder, or the loose files and then the largest file among them,
  down to that file's row. A drive has one pin at a time, so pinning another row moves it there;
  pinning takes over whatever is open below the row, and opening a folder by hand under the pin afterwards
  releases it (the button turns off); pin again to take the rows back over. The drive row's pin is its own: on a collapsed drive it starts the scan and
  opens the root under it, and it keeps the largest folders open below the drive just as a pin on the root row
  would, so the two are interchangeable but toggle independently. Close on a drive row collapses the drive. Every
  deletion asks for confirmation and names the path, size and counts. Drive roots cannot be deleted.
- **During a deletion** the row becomes a progress bar (`files · GB freed · elapsed`), everything else is
  locked, and the only live control is **Cancel**, which warns that the folder will be left partially
  deleted. Errors (for example a file held open by another program) are collected and listed when the job
  finishes; the folder is then rescanned so what is left is visible.
- **Resume.** Jobs are written to `jobs.json` in `%LOCALAPPDATA%\com.deleteman.app\` once a second.
  Unfinished jobs restart automatically when the app starts and are shown in the banner with a `resumed`
  tag. Closing the window while a deletion runs asks first; the job resumes next launch.

The window uses the Fluent UI and follows the system light/dark theme and accent colour.

The app runs as a normal user. Folders it cannot read are shown as access denied rather than silently
skipped. Junctions and symbolic links are never followed, neither while scanning nor while deleting: a
link inside a deleted folder is removed as an entry and its target is left untouched.

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

## Development

Needs the .NET 10 SDK (`global.json` pins the feature band). Visual Studio 2026 with the *Windows
application development* workload is optional for editing and debugging, but publishing with Native AOT
needs the MSVC linker from a Visual Studio (or Build Tools) install.

```
dotnet build DeleteMan.slnx
dotnet test --solution DeleteMan.slnx    # engine (scanner, deleter: read-only, >300-char paths, junction safety,
                                         # locked file, cancel; journal) and view-model tests
dotnet run --project src\DeleteMan.App   # debug build, framework-dependent on the installed Windows App Runtime
```

`global.json` selects `Microsoft.Testing.Platform` as the test runner, which is why `dotnet test` takes
`--solution` (or `--project`) rather than a bare path.

Diagnostics: the app rewrites `%LOCALAPPDATA%\com.deleteman.app\deleteman.log` on every start (trace output and
unhandled exceptions). Set `DELETEMAN_PERF=1` before launching to add timings to it: UI-thread stalls (frame
gaps over 40 ms with the phase before them), the time each poll phase takes, and a frame-rate / GC summary
every 2 s. Set it to a file path instead to write the timings there.

Projects: `src\DeleteMan.Engine` (scanner, deleter, journal, Recycle Bin), `src\DeleteMan.ViewModels`
(UI state, no WinUI dependency, unit-tested), `src\DeleteMan.App` (WinUI 3, unpackaged; the executable
is `deleteman.exe`), `src\DeleteMan.Bench` (command-line deleter for the benchmark), and `tests\`. The
version and shared settings are in `Directory.Build.props`, package versions in `Directory.Packages.props`.

Publish profiles, both Native AOT, in `src\DeleteMan.App\Properties\PublishProfiles\`:

- `fd-win-x64`: framework-dependent on the installed Windows App Runtime (the installer payload), output
  in `src\DeleteMan.App\bin\publish\fd-win-x64\`;
- `sc-win-x64`: self-contained, runtime files beside the exe (the portable zip), output in
  `src\DeleteMan.App\bin\publish\sc-win-x64\`.

```
dotnet publish src\DeleteMan.App -c Release -p:PublishProfile=sc-win-x64
```

**Publish from PowerShell, not Git Bash.** Git Bash drops `ProgramFiles(x86)` from the environment, and
Native AOT linking then cannot find Visual Studio. The scripts below restore it and put the Visual Studio
Installer folder on PATH before calling `dotnet publish`.

### Scripts

- `scripts\publish-check.ps1` publishes both profiles and reports errors, warnings, folder sizes, the
  framework-dependent file list and `deleteman.exe`'s native DLL imports.
- `scripts\screenshot-window.ps1 -Out shot.png` captures the running app's window to a PNG.
- `scripts\make-tree.ps1` builds a deterministic synthetic tree (read-only/hidden files, optional junction
  with a canary target, optional >300-character path, optional locked file).
- `scripts\bench.ps1` times the deletion engine (`DeleteMan.Bench.exe`, built by
  `scripts\publish-bench.ps1`) against `rmdir /s /q`, `Remove-Item -Recurse -Force` and
  `robocopy /MIR /MT:16` on identical fresh trees.
- `scripts\bump-version.ps1 <x.y.z>` sets the version, and `scripts\release.ps1` builds the release
  artifacts; see Releases below.

## Releases

The version lives in one place, the `<Version>` element of `Directory.Build.props` (`0.2.0` today). Every
project inherits it: the `v0.2.0` badge in the bottom-right corner of the window, the executables' file
properties, the zip name and the installer name. Change it with `scripts\bump-version.ps1 0.2.1` (or edit
the one line).

`scripts\release.ps1` (`-SkipBuild` reuses the last publish output, `-NoInstaller` skips the installer)
publishes both profiles and writes:

- `release\deleteman-<version>-win-x64.zip`: the self-contained publish output without `.pdb` files,
  `deleteman.exe` at the top level, plus `release\deleteman-<version>-win-x64.zip.sha256` in `sha256sum`
  format. Zips of other versions in `release\` are removed.
- `artifacts\Delete Man_<version>_x64-setup.exe`: the NSIS installer (`installer\deleteman.nsi`, compiled
  with `makensis`; install NSIS with `winget install NSIS.NSIS` if it is not found). Its payload is staged
  in `artifacts\installer\payload\`, and the Windows App Runtime installer is downloaded once into
  `artifacts\downloads\`. `artifacts\` is never committed.

Branches:

- `main` is development. It never carries binaries.
- `release/<major>.<minor>` (for example `release/0.2`) is cut from `main` for each minor version and holds
  the code **plus the portable zip**, `release/deleteman-<version>-win-x64.zip`, with its SHA-256 in
  `release/deleteman-<version>-win-x64.zip.sha256`. That zip is the file to send to people (or the
  installer from `artifacts\`). Patch releases (0.2.1, 0.2.2, ...) land on the same branch, which carries
  only its current build.

Cutting a release, from a clean and pushed `main` whose `Directory.Build.props` already has the new
version (`scripts\bump-version.ps1 0.3.0` to bump), in PowerShell:

```
git switch -c release/0.3               # cut from main
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1
git add release
git commit -m "Release 0.3.0: portable deleteman-0.3.0-win-x64.zip"
git push -u origin release/0.3
git switch main
```

Patching a release: fix on `main`, then on the release branch bring the fix over with `git merge main` or
`git cherry-pick <commit>`, run `scripts\bump-version.ps1 0.3.1`, rebuild with `scripts\release.ps1`, stage
(`git add release Directory.Build.props`), commit and push. Release branches are never merged back into `main`, which keeps
binaries out of it.

## Benchmark

`scripts\bench.ps1` with its defaults: identical fresh trees of 100,000 files in 10,000 folders (324 MiB,
5 % read-only, 1 % hidden+system, depth up to 12) on an NTFS NVMe volume, Windows 11. Each method was
verified to leave nothing behind. The first row is the C# engine; the Rust engine row (measured
2026-10-23 with `bench_delete.exe`) is the baseline the "vs" column compares against.

| Method | Seconds | Files/s | vs Rust engine |
|---|---:|---:|---:|
| C# engine, 2026-11-05 (`DeleteMan.Bench.exe`, 32 threads) | 16.6 | 6,021 | 1.2× faster |
| Rust engine, 2026-10-23 (`bench_delete.exe`, 32 threads) | 20.5 | 4,876 | 1.0× |
| `cmd /c rmdir /s /q` | 77.5 | 1,290 | 3.8× slower |
| `Remove-Item -Recurse -Force` | 107.5 | 930 | 5.2× slower |
| `robocopy <empty> <dir> /MIR /MT:16` | 106.7 | 937 | 5.2× slower |

The `rmdir`, `Remove-Item` and `robocopy` rows are from the same 2026-10-23 run. This machine has a
file-system filter driver that caps single-threaded metadata operations near 1,000 files per second, which
is why the single-threaded tools cluster there; the engine's gain comes from parallelism across subtrees
plus relative opens and POSIX delete semantics. Expect larger absolute numbers on an unfiltered system and
the same ordering.
