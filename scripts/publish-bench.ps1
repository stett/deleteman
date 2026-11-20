<#
.SYNOPSIS
    Publishes the deletion benchmark driver (src\DeleteMan.Bench) with Native AOT and prints the exe path.

.DESCRIPTION
    Runs `dotnet publish src\DeleteMan.Bench\DeleteMan.Bench.csproj -c Release -r win-x64` and reports where
    DeleteMan.Bench.exe was written. That path is the default -BenchExe of scripts\bench.ps1.
    Must run from PowerShell, not Git Bash (Native AOT linking needs environment variables that Git Bash drops;
    the script repairs the two known cases). Works on Windows PowerShell 5.1 and PowerShell 7+.
    Exit codes: 0 on success, 1 on any failure.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish-bench.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2

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

$project = Join-Path $root "src\DeleteMan.Bench\DeleteMan.Bench.csproj"
# Directory.Build.props sets Platform=x64, which adds the x64\ segment to the output path.
$exe = Join-Path $root "src\DeleteMan.Bench\bin\x64\Release\net10.0-windows\win-x64\publish\DeleteMan.Bench.exe"

try {
    Write-Host "Publishing DeleteMan.Bench (Native AOT)..."
    # Windows PowerShell turns native stderr lines into terminating errors under "Stop"; relax it for the build.
    $ErrorActionPreference = "Continue"
    & dotnet publish $project -c Release -r win-x64 -nologo
    $code = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($code -ne 0) { throw "dotnet publish failed with exit code $code" }
    # No timestamp check: an up-to-date incremental publish legitimately leaves the exe untouched.
    if (-not (Test-Path $exe)) { throw "publish finished but $exe does not exist" }
    Write-Host ""
    Write-Host "Published $exe"
    Write-Host "Run the benchmark with: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\bench.ps1"
    exit 0
}
catch {
    Write-Host "publish-bench: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
