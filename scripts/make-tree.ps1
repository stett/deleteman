<#
.SYNOPSIS
    Deterministic synthetic directory-tree generator for Delete Man deletion tests.

.DESCRIPTION
    Builds a bushy, skewed directory tree under -Root as fast as the file system
    allows. The same arguments (and -Seed) always produce the same shape, names,
    sizes and attributes.

    How: PowerShell computes the complete plan with System.Random (directory
    list, file -> directory assignment, sizes, attributes) in a single-threaded,
    deterministic pass; a small compiled helper (Add-Type, System.IO +
    Parallel.For) then creates directories level by level and writes the files
    with a preallocated byte array on several threads. Per-file create latency
    (file-system filter drivers, EDR) dominates on most machines, so threads
    help far more than shaving PowerShell overhead.

    Shape:
      * -Dirs directories are generated first by random parent selection
        (half uniform over all eligible parents -> bushy, half from the most
        recently created ones -> deep chains, occasionally a new top-level
        directory), never deeper than -MaxDepth.
      * -Files files are then distributed with a skewed distribution: 90 % go to
        a "hot" 10 % of the directories (power-law weighted, so a few directories
        hold hundreds of files), 10 % are spread uniformly, which leaves thousands
        of empty or nearly empty directories.
      * Every file is -FileSize bytes, except every 1000th (1 MiB) and every
        50000th (64 MiB).
      * -ReadOnlyPercent % of files get ReadOnly, 1 % of files get Hidden+System,
        1 % of directories get ReadOnly.

    Optional extras (not counted in -Dirs / -Files):
      -AddJunction  <Root>\links\to-outside -> <Root>-junction-target\ (sibling of
                    Root) containing CANARY.txt. After deleting <Root>, the target
                    and canary must still exist.
      -AddLongPath  <Root>\longpath\seg00-xxx...\... chain whose full path exceeds
                    300 characters, with a few files at the bottom (created via
                    \\?\ paths, independent of the LongPathsEnabled policy).
      -LockedFile   <Root>\locked\held-open.bin plus a one-liner you can run in a
                    second process to hold it open without FILE_SHARE_DELETE.

    The script never deletes anything: -Root must be missing or empty, and the
    junction target (with -AddJunction) must be missing or empty.

    Works on Windows PowerShell 5.1 and PowerShell 7+.
    Exit codes: 0 success, 1 failure.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-tree.ps1 -Root D:\projects\delete-man\testdata\big
    Default: 10,000 dirs, 100,000 files, depth <= 12, seed 42.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-tree.ps1 -Root D:\projects\delete-man\testdata\smoke -Dirs 200 -Files 2000 -AddJunction -AddLongPath
    Small tree with a junction escaping the tree and a >300 character path.

.EXAMPLE
    pwsh -File scripts\make-tree.ps1 -Root D:\projects\delete-man\testdata\locked -Dirs 10 -Files 50 -LockedFile
    Then run the printed snippet in another window to hold the file open.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [ValidateRange(0, 10000000)][int]$Dirs = 10000,
    [ValidateRange(0, 100000000)][int]$Files = 100000,
    [ValidateRange(1, 200)][int]$MaxDepth = 12,
    [ValidateRange(0, 1073741824)][int]$FileSize = 1024,
    [ValidateRange(0, 100)][int]$ReadOnlyPercent = 5,
    [int]$Seed = 42,
    [switch]$AddJunction,
    [switch]$AddLongPath,
    [switch]$LockedFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$CanaryText = 'DELETE-MAN-CANARY v1: this file lives OUTSIDE the generated tree. If it still exists after the tree was deleted, the junction was removed without being followed.'
$Threads = [Math]::Max(1, [Math]::Min(8, [Environment]::ProcessorCount))

# ---------------------------------------------------------------- helpers ---

# Compiled once per process (bench.ps1 calls this script repeatedly in-process).
if (-not ('DeleteManTreeV1' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

public static class DeleteManTreeV1 {
    // Creates paths[1..] level by level (parents before children), each level in parallel.
    public static void CreateDirectories(string[] paths, int[] depth, int maxDepth, int threads) {
        ParallelOptions opt = new ParallelOptions();
        opt.MaxDegreeOfParallelism = threads;
        for (int level = 1; level <= maxDepth; level++) {
            List<string> batch = new List<string>();
            for (int i = 1; i < paths.Length; i++) { if (depth[i] == level) { batch.Add(paths[i]); } }
            Parallel.ForEach(batch, opt, delegate(string p) { Directory.CreateDirectory(p); });
        }
    }

    // Writes paths[i] with buffers[sizeClass[i]] and applies attrs[i] (0 = leave as is).
    public static void WriteFiles(string[] paths, byte[] sizeClass, int[] attrs, byte[][] buffers, int threads) {
        ParallelOptions opt = new ParallelOptions();
        opt.MaxDegreeOfParallelism = threads;
        Parallel.For(0, paths.Length, opt, delegate(int i) {
            File.WriteAllBytes(paths[i], buffers[sizeClass[i]]);
            if (attrs[i] != 0) { File.SetAttributes(paths[i], (FileAttributes)attrs[i]); }
        });
    }

    // ---- Win32 fallback for \\?\ paths (Windows PowerShell 5.1 legacy path handling) ----
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr securityAttributes, uint disposition, uint flags, IntPtr template);

    public static void Win32CreateDirectory(string path) {
        if (!CreateDirectoryW(path, IntPtr.Zero)) {
            int err = Marshal.GetLastWin32Error();
            if (err != 183) { throw new Win32Exception(err, "CreateDirectoryW failed for " + path); }
        }
    }

    public static void Win32WriteAllBytes(string path, byte[] data) {
        // GENERIC_WRITE, no sharing, CREATE_NEW, FILE_ATTRIBUTE_NORMAL
        SafeFileHandle h = CreateFileW(path, 0x40000000, 0, IntPtr.Zero, 1, 0x80, IntPtr.Zero);
        if (h.IsInvalid) {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new Win32Exception(err, "CreateFileW failed for " + path);
        }
        using (FileStream fs = new FileStream(h, FileAccess.Write)) {
            fs.Write(data, 0, data.Length);
        }
    }
}
'@
}

function ConvertTo-ExtendedPath([string]$Path) {
    if ($Path.StartsWith('\\?\')) { return $Path }
    if ($Path.StartsWith('\\')) { return '\\?\UNC\' + $Path.Substring(2) }
    return '\\?\' + $Path
}

# Windows PowerShell 5.1 runs on .NET Framework, which may apply legacy path
# handling and reject '\\?\' paths ("Illegal characters in path"); whether it
# does was observed to vary between scripts in the same powershell.exe.
# PowerShell 7 (.NET Core) always accepts them. So: try System.IO first and, on
# a path-format exception, switch permanently to the Win32 fallback.
$script:NetExtendedPaths = $true

function Test-PathFormatException($Exception) {
    while ($null -ne $Exception) {
        if ($Exception -is [System.ArgumentException] -or $Exception -is [System.NotSupportedException] -or
            $Exception -is [System.IO.PathTooLongException]) { return $true }
        $Exception = $Exception.InnerException
    }
    return $false
}

function New-ExtendedDirectory([string]$ExtPath) {
    if ($script:NetExtendedPaths) {
        try { [void][System.IO.Directory]::CreateDirectory($ExtPath); return }
        catch { if (-not (Test-PathFormatException $_.Exception)) { throw } }
        $script:NetExtendedPaths = $false
    }
    [DeleteManTreeV1]::Win32CreateDirectory($ExtPath)
}

function Write-ExtendedFile([string]$ExtPath, [byte[]]$Data) {
    if ($script:NetExtendedPaths) {
        try { [System.IO.File]::WriteAllBytes($ExtPath, $Data); return }
        catch { if (-not (Test-PathFormatException $_.Exception)) { throw } }
        $script:NetExtendedPaths = $false
    }
    [DeleteManTreeV1]::Win32WriteAllBytes($ExtPath, $Data)
}

function Test-DirectoryEmpty([string]$Path) {
    $enumerator = [System.IO.Directory]::EnumerateFileSystemEntries($Path).GetEnumerator()
    try { return (-not $enumerator.MoveNext()) } finally { $enumerator.Dispose() }
}

function Invoke-NativeQuiet([scriptblock]$Block) {
    # Native stderr must not become a terminating error under 'Stop' (PS 5.1).
    $ErrorActionPreference = 'Continue'
    & $Block 2>&1 | ForEach-Object { "$_" }
}

function Format-Bytes([double]$Bytes) {
    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    if ($Bytes -ge 1GB) { return ($Bytes / 1GB).ToString('0.00', $inv) + ' GiB' }
    if ($Bytes -ge 1MB) { return ($Bytes / 1MB).ToString('0.00', $inv) + ' MiB' }
    if ($Bytes -ge 1KB) { return ($Bytes / 1KB).ToString('0.00', $inv) + ' KiB' }
    return ([long]$Bytes).ToString() + ' B'
}

function Get-InnermostMessage($Exception) {
    while ($null -ne $Exception.InnerException) { $Exception = $Exception.InnerException }
    return $Exception.Message
}

# ------------------------------------------------------------------- main ---

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    # Resolve relative to the PowerShell location (not the process CWD).
    $Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
    $Root = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $pathRoot = [System.IO.Path]::GetPathRoot($Root + '\').TrimEnd('\')
    if ($Root.Length -eq 0 -or $Root -eq $pathRoot) { throw "Refusing to use a drive/share root as -Root: '$Root'." }

    if ([System.IO.File]::Exists($Root)) { throw "-Root '$Root' exists and is a file." }
    if ([System.IO.Directory]::Exists($Root) -and -not (Test-DirectoryEmpty $Root)) {
        throw "-Root '$Root' exists and is not empty. This script never deletes anything; pick a new path or remove it yourself."
    }
    $junctionTarget = $Root + '-junction-target'
    if ($AddJunction) {
        if ([System.IO.File]::Exists($junctionTarget)) { throw "Junction target '$junctionTarget' exists and is a file." }
        if ([System.IO.Directory]::Exists($junctionTarget) -and -not (Test-DirectoryEmpty $junctionTarget)) {
            throw "Junction target '$junctionTarget' exists and is not empty; refusing to continue."
        }
    }

    Write-Host ("make-tree: root={0} dirs={1} files={2} maxDepth={3} fileSize={4} readOnly={5}% seed={6} threads={7}" -f `
            $Root, $Dirs, $Files, $MaxDepth, $FileSize, $ReadOnlyPercent, $Seed, $Threads)
    [void][System.IO.Directory]::CreateDirectory($Root)

    $rng = New-Object System.Random($Seed)
    $contentRng = New-Object System.Random($Seed + 1)

    # ---- 1. plan directories (index 0 = Root) -------------------------------
    $words = @('src', 'assets', 'cache', 'build', 'logs', 'data', 'tmp', 'pkg', 'obj', 'docs', 'img', 'lib', 'test', 'vendor', 'out', 'bin')
    $total = $Dirs + 1
    $relPath = New-Object 'string[]' $total
    $fullPath = New-Object 'string[]' $total
    $depth = New-Object 'int[]' $total
    $top = New-Object 'int[]' $total
    $relPath[0] = ''
    $fullPath[0] = $Root
    $candidates = New-Object 'System.Collections.Generic.List[int]'
    $candidates.Add(0)
    $maxSeenDepth = 0
    for ($i = 1; $i -le $Dirs; $i++) {
        $roll = $rng.Next(200)
        if ($roll -eq 0) {
            $parent = 0                                             # occasional new top-level dir
        } elseif ($roll -lt 100) {
            $parent = $candidates[$rng.Next($candidates.Count)]     # bushy
        } else {
            $window = [Math]::Min(16, $candidates.Count)            # deep chains
            $parent = $candidates[$candidates.Count - 1 - $rng.Next($window)]
        }
        $name = $words[$rng.Next($words.Length)] + '-' + $i
        if ($parent -eq 0) { $relPath[$i] = $name; $top[$i] = $i }
        else { $relPath[$i] = $relPath[$parent] + '\' + $name; $top[$i] = $top[$parent] }
        $fullPath[$i] = $Root + '\' + $relPath[$i]
        $d = $depth[$parent] + 1
        $depth[$i] = $d
        if ($d -gt $maxSeenDepth) { $maxSeenDepth = $d }
        if ($d -lt $MaxDepth) { $candidates.Add($i) }
    }

    # ---- 2. plan files --------------------------------------------------------
    # Shuffle directory indices; the first 10 % are the "hot" directories.
    $perm = New-Object 'int[]' $total
    for ($i = 0; $i -lt $total; $i++) { $perm[$i] = $i }
    for ($i = $total - 1; $i -gt 0; $i--) {
        $j = $rng.Next($i + 1)
        $tmp = $perm[$i]; $perm[$i] = $perm[$j]; $perm[$j] = $tmp
    }
    $hotCount = [Math]::Max(1, [int][Math]::Ceiling($total * 0.1))

    $small = New-Object 'byte[]' $FileSize
    $contentRng.NextBytes($small)
    $buffers = New-Object 'byte[][]' 3
    $buffers[0] = $small
    if ($Files -ge 1000) { $buffers[1] = New-Object 'byte[]' (1MB); $contentRng.NextBytes($buffers[1]) }
    if ($Files -ge 50000) { $buffers[2] = New-Object 'byte[]' (64MB); $contentRng.NextBytes($buffers[2]) }
    $sizeOf = @([long]$FileSize, [long]1MB, [long]64MB)

    $exts = @('.dat', '.txt', '.bin', '.json', '.log', '.obj', '.png', '.cache')
    $filePath = New-Object 'string[]' $Files
    $fileClass = New-Object 'byte[]' $Files
    $fileAttrs = New-Object 'int[]' $Files
    $filesPerDir = New-Object 'int[]' $total
    $filesPerTop = New-Object 'long[]' $total
    $bytesPerTop = New-Object 'long[]' $total
    $totalBytes = [long]0
    $readOnlyFiles = 0
    $hiddenSystemFiles = 0
    $attrReadOnly = [int][System.IO.FileAttributes]::ReadOnly
    $attrHiddenSystem = [int]([System.IO.FileAttributes]::Hidden -bor [System.IO.FileAttributes]::System)

    for ($x = 0; $x -lt $Files; $x++) {
        $n = $x + 1
        if ($rng.Next(10) -lt 9) {
            $k = [int][Math]::Floor($hotCount * [Math]::Pow($rng.NextDouble(), 1.5))
            $dirIndex = $perm[$k]
        } else {
            $dirIndex = $rng.Next($total)
        }
        $filePath[$x] = $fullPath[$dirIndex] + '\f' + $n + $exts[$rng.Next($exts.Length)]

        if ($n % 50000 -eq 0) { $class = 2 } elseif ($n % 1000 -eq 0) { $class = 1 } else { $class = 0 }
        $fileClass[$x] = $class

        # Always draw both numbers so the sequence does not depend on outcomes.
        $attrs = 0
        if ($rng.Next(100) -lt $ReadOnlyPercent) { $attrs = $attrReadOnly; $readOnlyFiles++ }
        if ($rng.Next(100) -eq 0) { $attrs = $attrs -bor $attrHiddenSystem; $hiddenSystemFiles++ }
        $fileAttrs[$x] = $attrs

        $len = $sizeOf[$class]
        $totalBytes += $len
        $filesPerDir[$dirIndex]++
        $t = $top[$dirIndex]
        $filesPerTop[$t]++
        $bytesPerTop[$t] += $len
    }
    Write-Host ("  planned {0} dirs / {1} files ({2:0.0}s)" -f $Dirs, $Files, $stopwatch.Elapsed.TotalSeconds)

    # ---- 3. create ------------------------------------------------------------
    [DeleteManTreeV1]::CreateDirectories($fullPath, $depth, $MaxDepth, $Threads)
    Write-Host ("  created {0} directories ({1:0.0}s)" -f $Dirs, $stopwatch.Elapsed.TotalSeconds)
    [DeleteManTreeV1]::WriteFiles($filePath, $fileClass, $fileAttrs, $buffers, $Threads)
    Write-Host ("  wrote {0} files ({1:0.0}s)" -f $Files, $stopwatch.Elapsed.TotalSeconds)

    # Read-only directories last, so nothing above is affected by them.
    $readOnlyDirs = 0
    for ($i = 1; $i -le $Dirs; $i++) {
        if ($rng.Next(100) -eq 0) {
            $a = [System.IO.File]::GetAttributes($fullPath[$i]) -bor [System.IO.FileAttributes]::ReadOnly
            [System.IO.File]::SetAttributes($fullPath[$i], $a)
            $readOnlyDirs++
        }
    }

    # ---- 4. extras ------------------------------------------------------------
    $extraDirs = 0
    $extraFiles = 0
    $extraNotes = New-Object 'System.Collections.Generic.List[string]'

    if ($AddJunction) {
        $linksDir = $Root + '\links'
        $linkPath = $linksDir + '\to-outside'
        [void][System.IO.Directory]::CreateDirectory($linksDir)
        [void][System.IO.Directory]::CreateDirectory($junctionTarget)
        $canaryPath = $junctionTarget + '\CANARY.txt'
        [System.IO.File]::WriteAllText($canaryPath, $CanaryText)
        $out = Invoke-NativeQuiet { & cmd.exe /c mklink /J $linkPath $junctionTarget }
        if ($LASTEXITCODE -ne 0) { throw ("mklink /J failed (exit {0}): {1}" -f $LASTEXITCODE, ($out -join ' ')) }
        $extraDirs += 2   # links + the junction entry itself
        $extraNotes.Add("junction : $linkPath -> $junctionTarget")
        $extraNotes.Add("CANARY   : $canaryPath")
        $extraNotes.Add("canary content: $CanaryText")
    }

    if ($AddLongPath) {
        $longBase = ConvertTo-ExtendedPath ($Root + '\longpath')
        $prefixLen = $longBase.Length - ($Root + '\longpath').Length
        New-ExtendedDirectory $longBase
        $extraDirs++
        $cur = $longBase
        $seg = 0
        while (($cur.Length - $prefixLen) -le 300) {
            $segName = ('seg{0:D2}-' -f $seg) + ('x' * 54)   # 60 characters
            $cur = $cur + '\' + $segName
            New-ExtendedDirectory $cur
            $extraDirs++
            $seg++
        }
        for ($f = 1; $f -le 3; $f++) {
            Write-ExtendedFile ($cur + '\deep-file-' + $f + '.bin') $small
            $extraFiles++
            $totalBytes += $small.Length
        }
        $deepFile = $cur.Substring($prefixLen) + '\deep-file-3.bin'
        $extraNotes.Add(("long path: {0} segments, deepest dir {1} chars, deepest file {2} chars" -f `
                    $seg, ($cur.Length - $prefixLen), $deepFile.Length))
        $how = 'Win32 CreateDirectoryW/CreateFileW fallback (System.IO rejected \\?\)'
        if ($script:NetExtendedPaths) { $how = 'System.IO with \\?\ paths' }
        $extraNotes.Add("           (created via: $how)")
    }

    if ($LockedFile) {
        $lockDir = $Root + '\locked'
        $lockPath = $lockDir + '\held-open.bin'
        [void][System.IO.Directory]::CreateDirectory($lockDir)
        $lockData = New-Object 'byte[]' (64KB)
        $contentRng.NextBytes($lockData)
        [System.IO.File]::WriteAllBytes($lockPath, $lockData)
        $extraDirs++
        $extraFiles++
        $totalBytes += $lockData.Length
        $escaped = $lockPath.Replace("'", "''")
        $extraNotes.Add("locked   : $lockPath")
        $extraNotes.Add('hold it open (no FILE_SHARE_DELETE) from another process with:')
        $extraNotes.Add("  powershell -NoProfile -Command `"`$f=[System.IO.File]::Open('$escaped','Open','Read','Read'); Start-Sleep -Seconds 3600; `$f.Close()`"")
    }

    $stopwatch.Stop()

    # ---- 5. summary -----------------------------------------------------------
    $emptyDirs = 0
    $maxFilesInDir = 0
    for ($i = 0; $i -lt $total; $i++) {
        if ($filesPerDir[$i] -eq 0) { $emptyDirs++ }
        if ($filesPerDir[$i] -gt $maxFilesInDir) { $maxFilesInDir = $filesPerDir[$i] }
    }
    $dirsPerTop = New-Object 'int[]' $total
    for ($i = 1; $i -le $Dirs; $i++) { $dirsPerTop[$top[$i]]++ }
    $topLevel = New-Object 'System.Collections.Generic.List[object]'
    for ($i = 1; $i -le $Dirs; $i++) {
        if ($depth[$i] -eq 1) {
            $topLevel.Add([pscustomobject]@{ Name = $relPath[$i]; Dirs = $dirsPerTop[$i]; Files = $filesPerTop[$i]; Bytes = $bytesPerTop[$i] })
        }
    }

    Write-Host ''
    Write-Host '=== make-tree summary ==='
    Write-Host ("root          : {0}" -f $Root)
    Write-Host ("dirs          : {0} generated (+{1} extra), max depth {2}, {3} without files, {4} read-only" -f $Dirs, $extraDirs, $maxSeenDepth, $emptyDirs, $readOnlyDirs)
    Write-Host ("files         : {0} generated (+{1} extra), max {2} in one dir, {3} read-only, {4} hidden+system" -f $Files, $extraFiles, $maxFilesInDir, $readOnlyFiles, $hiddenSystemFiles)
    Write-Host ("total bytes   : {0} ({1})" -f $totalBytes, (Format-Bytes $totalBytes))
    Write-Host ("elapsed       : {0:0.00} s" -f $stopwatch.Elapsed.TotalSeconds)
    $rootFilesNote = ''
    if ($filesPerTop[0] -gt 0) { $rootFilesNote = ", $($filesPerTop[0]) files directly in root" }
    Write-Host ("top-level     : {0} generated directories{1} (largest first, dirs/files are recursive)" -f $topLevel.Count, $rootFilesNote)
    $shown = 0
    foreach ($entry in ($topLevel | Sort-Object -Property Files -Descending)) {
        if ($shown -ge 15) { Write-Host ("  ... {0} more" -f ($topLevel.Count - $shown)); break }
        Write-Host ("  {0,-16} {1,7} dirs {2,8} files {3,12}" -f $entry.Name, $entry.Dirs, $entry.Files, (Format-Bytes $entry.Bytes))
        $shown++
    }
    if ($AddJunction) { Write-Host '  links\            (junction, see below)' }
    if ($AddLongPath) { Write-Host '  longpath\         (>300 char chain, see below)' }
    if ($LockedFile) { Write-Host '  locked\           (lock-test file, see below)' }
    foreach ($note in $extraNotes) { Write-Host $note }
    exit 0
} catch {
    [Console]::Error.WriteLine('make-tree: ERROR: ' + (Get-InnermostMessage $_.Exception))
    exit 1
}
