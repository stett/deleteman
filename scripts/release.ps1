<#
.SYNOPSIS
    Builds the Delete Man release artifacts: the portable zip in release\ and the installer in artifacts\.

.DESCRIPTION
    Reads the version from Directory.Build.props (the single source of truth) and then:

    1. Portable zip. `dotnet publish src\DeleteMan.App -c Release -p:PublishProfile=sc-win-x64` (Native AOT,
       Windows App Runtime files beside the exe) and zips the publish folder, without *.pdb files, to

           release\deleteman-<version>-win-x64.zip          (deleteman.exe at the top level of the zip)
           release\deleteman-<version>-win-x64.zip.sha256   (sha256sum format)

       Zips and checksums of other versions already in release\ are removed, so a release branch carries
       exactly one build.

    2. Installer (skipped with -NoInstaller). `dotnet publish ... -p:PublishProfile=fd-win-x64` (Native AOT,
       framework-dependent on the installed Windows App Runtime), stages the payload in
       artifacts\installer\payload\ (publish output without *.pdb + installer\path-helper\*.ps1 + the Windows
       App Runtime 2.6 redistributable installer, downloaded once into artifacts\downloads\), and compiles
       installer\deleteman.nsi with makensis into

           artifacts\Delete Man_<version>_x64-setup.exe

       The installer is not committed anywhere; publish it however you share files.

    Both published deleteman.exe files must report a ProductVersion starting with the version.

    The release\ folder is committed only on release branches (release/<major>.<minor>); main never carries
    binaries. See README.md, section "Releases".

    Must run from PowerShell, not Git Bash (Native AOT linking needs environment variables that Git Bash drops;
    the script repairs the two known cases). Works on Windows PowerShell 5.1 and PowerShell 7+.
    Exit codes: 0 on success, 1 on any failure.

.PARAMETER SkipBuild
    Reuse the existing src\DeleteMan.App\bin\publish\sc-win-x64 (and fd-win-x64) folders instead of publishing.

.PARAMETER NoInstaller
    Only produce the portable zip; skip the framework-dependent publish, the payload and makensis.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1
    Full build: release\deleteman-0.2.0-win-x64.zip (+ .sha256) and artifacts\Delete Man_0.2.0_x64-setup.exe.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1 -SkipBuild -NoInstaller
    Re-zip the last self-contained publish without compiling.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$NoInstaller
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2

# Windows App SDK 2.6 runtime installer (x64), from https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads.
# Keep the line in step with the Microsoft.WindowsAppSDK package version in Directory.Packages.props.
$RuntimeUrl = "https://aka.ms/windowsappsdk/2.6/2.6.5/windowsappruntimeinstall-x64.exe"
$RuntimeExe = "WindowsAppRuntimeInstall-x64.exe"

$root = Split-Path -Parent $PSScriptRoot

function Format-Size([long]$bytes) { "{0:N1} MB" -f ($bytes / 1MB) }

# Runs `dotnet publish` for one profile and checks that the output was (re)written.
function Invoke-Publish([string]$profileName, [string]$outDir) {
    $project = Join-Path $root "src\DeleteMan.App\DeleteMan.App.csproj"
    $started = Get-Date
    Write-Host "Publishing Delete Man $version ($profileName)..."
    # Windows PowerShell turns native stderr lines into terminating errors under "Stop" when stderr is
    # redirected; relax it for the duration of the build.
    $ErrorActionPreference = "Continue"
    & dotnet publish $project -c Release "-p:PublishProfile=$profileName" -nologo
    $code = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($code -ne 0) { throw "dotnet publish ($profileName) failed with exit code $code" }
    $exe = Join-Path $outDir "deleteman.exe"
    if (-not (Test-Path $exe) -or (Get-Item $exe).LastWriteTime -lt $started) {
        throw "publish finished but $exe was not (re)written"
    }
}

# The publish output embeds <Version> from Directory.Build.props; a mismatch means the build is stale.
function Assert-ExeVersion([string]$exe) {
    if (-not (Test-Path $exe)) { throw "$exe not found; run without -SkipBuild" }
    $info = (Get-Item $exe).VersionInfo
    $embedded = if ($info.ProductVersion) { $info.ProductVersion } else { $info.FileVersion }
    if (-not "$embedded".StartsWith($version)) {
        throw "$exe reports version '$embedded' but Directory.Build.props says '$version'; rebuild without -SkipBuild"
    }
}

# Copies a publish folder to $dest without the *.pdb files (they stay where dotnet publish left them).
function Copy-WithoutPdb([string]$src, [string]$dest) {
    New-Item -ItemType Directory -Force $dest | Out-Null
    $srcFull = (Resolve-Path $src).Path.TrimEnd('\')
    foreach ($f in @(Get-ChildItem $srcFull -Recurse -File | Where-Object { $_.Extension -ne ".pdb" })) {
        $target = Join-Path $dest $f.FullName.Substring($srcFull.Length + 1)
        $dir = Split-Path -Parent $target
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
        Copy-Item -LiteralPath $f.FullName -Destination $target -Force
    }
}

# makensis: PATH, then the copy the old Tauri toolchain left in %LOCALAPPDATA%\tauri\NSIS.
function Find-Makensis {
    $cmd = Get-Command makensis -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @(
            (Join-Path $env:LOCALAPPDATA "tauri\NSIS\makensis.exe"),
            (Join-Path $env:LOCALAPPDATA "tauri\NSIS\Bin\makensis.exe"),
            (Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe"),
            (Join-Path $env:ProgramFiles "NSIS\makensis.exe"))) {
        if ($p -and (Test-Path $p)) { return $p }
    }
    throw "makensis not found; install NSIS: winget install NSIS.NSIS"
}

Push-Location $root
try {
    $props = Join-Path $root "Directory.Build.props"
    $m = [regex]::Match([System.IO.File]::ReadAllText($props), '<Version>\s*([^<]*?)\s*</Version>')
    if (-not $m.Success) { throw "Directory.Build.props has no <Version> element" }
    $version = $m.Groups[1].Value
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Directory.Build.props version '$version' is not of the form x.y.z"
    }

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

    $publishRoot = Join-Path $root "src\DeleteMan.App\bin\publish"
    $scDir = Join-Path $publishRoot "sc-win-x64"
    $fdDir = Join-Path $publishRoot "fd-win-x64"

    # ---- 1. Portable zip ------------------------------------------------------------------------------------
    if (-not $SkipBuild) { Invoke-Publish "sc-win-x64" $scDir }
    Assert-ExeVersion (Join-Path $scDir "deleteman.exe")

    $outDir = Join-Path $root "release"
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $name = "deleteman-$version-win-x64.zip"
    $zip = Join-Path $outDir $name
    $sumOut = "$zip.sha256"

    # A release branch carries only the current build (this also drops the Rust-era deleteman-<ver>.exe files).
    foreach ($f in @(Get-ChildItem $outDir -File -Filter "deleteman-*")) {
        if ($f.Name -eq $name -or $f.Name -eq "$name.sha256") { continue }
        if ($f.Name -notmatch '\.(zip|exe)(\.sha256)?$') { continue }
        Remove-Item $f.FullName
        Write-Host "removed stale $($f.Name)"
    }

    # Zip the publish folder's contents, without PDBs, so deleteman.exe sits at the zip root. Entries are added one
    # by one with "/" separators: ZipFile.CreateFromDirectory on Windows PowerShell 5.1 (.NET Framework) writes "\"
    # into entry names, which the zip format does not allow and some extractors turn into odd file names.
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $scFull = (Resolve-Path $scDir).Path.TrimEnd('\')
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in @(Get-ChildItem $scFull -Recurse -File | Where-Object { $_.Extension -ne ".pdb" })) {
            $entry = $f.FullName.Substring($scFull.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $archive.Dispose()
    }

    $hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    $utf8 = New-Object System.Text.UTF8Encoding $false   # Set-Content -Encoding UTF8 writes a BOM on 5.1
    [System.IO.File]::WriteAllText($sumOut, "$hash  $name`n", $utf8)


    # ---- 2. Installer ---------------------------------------------------------------------------------------
    $setup = $null
    if (-not $NoInstaller) {
        if (-not $SkipBuild) { Invoke-Publish "fd-win-x64" $fdDir }
        Assert-ExeVersion (Join-Path $fdDir "deleteman.exe")

        # The Windows App Runtime redistributable, downloaded once.
        $downloads = Join-Path $root "artifacts\downloads"
        New-Item -ItemType Directory -Force $downloads | Out-Null
        $runtime = Join-Path $downloads $RuntimeExe
        if (-not (Test-Path $runtime)) {
            Write-Host "Downloading $RuntimeUrl ..."
            $tmp = "$runtime.part"
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $ProgressPreference = "SilentlyContinue"   # the progress bar makes Invoke-WebRequest very slow on 5.1
            Invoke-WebRequest -Uri $RuntimeUrl -OutFile $tmp -UseBasicParsing
            $sig = Get-AuthenticodeSignature $tmp
            if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notlike "CN=Microsoft Corporation*") {
                Remove-Item $tmp -Force
                throw "downloaded $RuntimeExe is not validly signed by Microsoft (status $($sig.Status))"
            }
            Move-Item $tmp $runtime -Force
        }

        # Payload = fd publish output (no PDBs) + PATH helpers + runtime installer.
        $payload = Join-Path $root "artifacts\installer\payload"
        if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
        Copy-WithoutPdb $fdDir $payload
        $helperDir = Join-Path $payload "path-helper"
        New-Item -ItemType Directory -Force $helperDir | Out-Null
        Copy-Item (Join-Path $root "installer\path-helper\*.ps1") $helperDir
        Copy-Item $runtime $payload

        $makensis = Find-Makensis
        $setup = Join-Path $root "artifacts\Delete Man_${version}_x64-setup.exe"
        if (Test-Path $setup) { Remove-Item $setup -Force }
        Write-Host "Compiling installer with $makensis ..."
        $ErrorActionPreference = "Continue"
        & $makensis /V2 /INPUTCHARSET UTF8 "/DVERSION=$version" "/DPAYLOAD=$payload" "/DOUTFILE=$setup" (Join-Path $root "installer\deleteman.nsi")
        $code = $LASTEXITCODE
        $ErrorActionPreference = "Stop"
        if ($code -ne 0) { throw "makensis failed with exit code $code" }
        if (-not (Test-Path $setup)) { throw "makensis finished but $setup was not written" }
    }

    # ---- Summary --------------------------------------------------------------------------------------------
    Write-Host ""
    Write-Host "Delete Man $version"
    Write-Host "  $zip  ($(Format-Size (Get-Item $zip).Length))"
    Write-Host "    SHA-256 $hash"
    if ($setup) {
        $setupHash = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()
        Write-Host "  $setup  ($(Format-Size (Get-Item $setup).Length))"
        Write-Host "    SHA-256 $setupHash"
    }
    Write-Host ""
    $minor = $version -replace '\.\d+$', ''
    Write-Host "Next, on branch release/${minor}:"
    Write-Host "  git add release"
    Write-Host "  git commit -m `"Release ${version}: portable deleteman-$version-win-x64.zip`""
    Write-Host "  git push -u origin release/$minor"
    exit 0
}
catch {
    Write-Host "release: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
