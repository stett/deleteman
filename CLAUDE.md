# Delete Man: notes for coding agents

Windows disk-usage explorer and fast deleter. C# / WinUI 3 on .NET 10, Native AOT, unpackaged, no web view.
`src\DeleteMan.Engine` (scanner, deleter, journal, Recycle Bin), `src\DeleteMan.ViewModels` (all UI state,
no WinUI dependency, unit-tested), `src\DeleteMan.App` (WinUI 3 shell), `tests\`.

Read first: `docs/development.md` (build, test, publish, release), `docs/ui.md` (what every row, segment
and button does; the view-model tests enforce it), `docs/engine.md` (scanner and deleter internals).

Rules that have cost time:

- Build with `dotnet build DeleteMan.slnx`. Test with `dotnet test --solution DeleteMan.slnx`, or
  `dotnet test --project <test project dir>` for one project. Never add `--no-build`: it runs stale DLLs
  that the solution build does not refresh.
- `EngineHostTests.Second_job_is_refused_while_one_runs_and_cancel_stops_it` is flaky in a full solution
  run and passes alone; it is a race in the test, not a product bug.
- Publish (Native AOT) from PowerShell, never Git Bash: `scripts\publish-check.ps1` or `scripts\release.ps1`.
- UI work is not done until the *published* exe has been run. The Debug (JIT) build hides AOT-only
  failures: casting WinRT objects from resource dictionaries (use `.As<T>()`, see `Controls\AppResources.cs`)
  and `Content` items that `dotnet publish` drops without `CopyToOutputDirectory`.
- The app is single-instance: launching it while a copy runs only focuses that copy. Close test instances by
  PID or exe path, never `taskkill /IM deleteman.exe`.
- The app rewrites `%LOCALAPPDATA%\com.deleteman.app\deleteman.log` on every start; read it when the window
  comes up blank. `DELETEMAN_PERF=1` adds frame-gap and poll timings.
- Behaviour changes to rows, segments or the pin go with their tests in `tests\DeleteMan.ViewModels.Tests`
  and with `docs/ui.md`.
- Version control is git (GitHub `stett/deleteman`). A release is a tag `vX.Y.Z` plus a GitHub release
  carrying the zip, its `.sha256` and the installer; see `docs/development.md`.
