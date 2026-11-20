<#
.SYNOPSIS
    Publishes the app with both profiles (framework-dependent and self-contained, Native AOT) and reports what came out.

.DESCRIPTION
    Development helper. Runs `dotnet publish` for Properties\PublishProfiles\fd-win-x64.pubxml and sc-win-x64.pubxml,
    prints errors/warnings, the size and file count of each output folder, the files of the framework-dependent
    output and the native DLL dependencies of deleteman.exe (via dumpbin when Visual Studio is installed).
    Must run from PowerShell (Native AOT linking needs the Visual Studio environment variables, which Git Bash drops).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish-check.ps1
#>
[CmdletBinding()]
param(
    [string[]]$Profiles = @('fd-win-x64', 'sc-win-x64'),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot

# Native AOT linking locates Visual Studio through vswhere.exe under %ProgramFiles(x86)%. A shell that cannot
# represent that variable name (Git Bash, some CI runners) drops it from the environment it passes on, and the
# ILCompiler targets then fail with "'vswhere.exe' is not recognized". Restore it when missing.
if (-not ${env:ProgramFiles(x86)}) {
    [Environment]::SetEnvironmentVariable('ProgramFiles(x86)', 'C:\Program Files (x86)', 'Process')
}
if (-not ${env:CommonProgramFiles(x86)}) {
    [Environment]::SetEnvironmentVariable('CommonProgramFiles(x86)', 'C:\Program Files (x86)\Common Files', 'Process')
}
# Visual Studio 2026's vcvarsall.bat calls vswhere.exe by bare name. When the Installer folder is not on PATH the
# complaint goes to stderr, ILCompiler's linker-discovery step captures it into the linker path list, and link.exe
# fails with exit code 123. Prepending the folder makes vcvarsall quiet.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $vsInstaller) -and ($env:Path -notlike "*$vsInstaller*")) {
    $env:Path = "$vsInstaller;$env:Path"
}
$project = Join-Path $root 'src\DeleteMan.App\DeleteMan.App.csproj'
$publishRoot = Join-Path $root 'src\DeleteMan.App\bin\publish'

foreach ($profile in $Profiles) {
    if (-not $SkipBuild) {
        Write-Host "=== publish $profile ==="
        $out = & dotnet publish $project -c Release "-p:PublishProfile=$profile" 2>&1 | ForEach-Object { "$_" }
        $out | Where-Object { $_ -match 'error|warning' } | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) { Write-Host "publish $profile FAILED (exit $LASTEXITCODE)" }
        else { Write-Host "publish $profile ok" }
    }
    $dir = Join-Path $publishRoot $profile
    if (Test-Path $dir) {
        $files = Get-ChildItem $dir -Recurse -File
        $mb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
        Write-Host ("{0}: {1} MB, {2} files" -f $profile, $mb, $files.Count)
    }
}

$fd = Join-Path $publishRoot 'fd-win-x64'
if (Test-Path $fd) {
    Write-Host "=== fd-win-x64 files ==="
    Get-ChildItem $fd -File | ForEach-Object { Write-Host ("{0,12:N0}  {1}" -f $_.Length, $_.Name) }
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -property installationPath
    $dumpbin = Get-ChildItem (Join-Path $vs 'VC\Tools\MSVC') -Recurse -Filter dumpbin.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'Hostx64\\x64' } | Select-Object -First 1
    $exe = Join-Path $fd 'deleteman.exe'
    if ($dumpbin -and (Test-Path $exe)) {
        Write-Host "=== deleteman.exe (fd) imports ==="
        & $dumpbin.FullName /dependents $exe | Where-Object { $_ -match '\.dll' } | ForEach-Object { Write-Host $_.Trim() }
    }
}
