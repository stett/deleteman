# Development

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

## Scripts

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
  `artifacts\downloads\`.

Neither folder is committed. Binaries ship as GitHub releases, one per version, on a tag `v<version>` of
`main`. From a clean, pushed `main` whose `Directory.Build.props` already has the new version, in
PowerShell (`release.ps1` prints these lines with the right names when it finishes):

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1
git tag -a v0.3.0 -m "Delete Man 0.3.0"
git push origin v0.3.0
gh release create v0.3.0 "release\deleteman-0.3.0-win-x64.zip" "release\deleteman-0.3.0-win-x64.zip.sha256" "artifacts\Delete Man_0.3.0_x64-setup.exe" --title "Delete Man 0.3.0" --notes-file notes.md --verify-tag --latest
```

Put both SHA-256 hashes (printed by `release.ps1`) in the notes. GitHub replaces the space in the
installer's file name with a dot. To patch an older minor, branch from its tag
(`git switch -c release/0.2 v0.2.0`), cherry-pick the fix, bump the version and release from that branch
the same way; no branch carries binaries.

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

## History

Version 0.1 was written in Rust + Tauri 2. Those sources are not in this repository: they stayed in the Lore
repository the project used before it moved to git (branches `legacy/rust-tauri` and `release/0.1`). The
engine algorithms were ported to C# one to one.

Installing 0.2 over the 0.1 (Tauri) install replaces it in place.
