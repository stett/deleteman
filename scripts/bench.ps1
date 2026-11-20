<#
.SYNOPSIS
    Deletion benchmark for Delete Man: compares DeleteMan.Bench.exe against rmdir,
    Remove-Item and robocopy /MIR on identical synthetic trees.

.DESCRIPTION
    For each method x repeat:
      1. creates a fresh tree at <Work>\tree-<method>-<n> with make-tree.ps1
         (same seed every time, no junction / long path / locked file),
      2. waits 2 s,
      3. times the deletion with System.Diagnostics.Stopwatch,
      4. verifies the tree is gone (FAILED + number of remaining entries if not).

    Methods:
      delete-man   & <BenchExe> <path>                     (skipped if the exe is missing;
                   build it with scripts\publish-bench.ps1)
      rmdir        cmd /c rmdir /s /q <path>
      remove-item  Remove-Item -LiteralPath <path> -Recurse -Force
      robocopy     robocopy <Work>\empty <path> /MIR /MT:16 /NFL /NDL /NJH /NJS /NP /R:0 /W:0 /LOG:NUL
                   (exit < 8 = ok), then cmd /c rmdir /q <path>. /LOG:NUL is needed because
                   with /MT robocopy still lists every *EXTRA entry on stdout.

    Prints a markdown table (Method | Files | Dirs | Seconds | Files/s | OK), the
    fastest method and the speedup of delete-man versus every other method, and
    writes the same report to <Work>\results-<timestamp>.md.

    Safety: refuses to run if -Work resolves to a drive root or a path shorter
    than 10 characters, and never deletes anything outside <Work>\tree-*.
    Failed trees are left in place for inspection.

    Works on Windows PowerShell 5.1 and PowerShell 7+.
    Exit codes: 0 all runs OK (skipped methods are not failures), 1 otherwise.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\bench.ps1
    Full default run: 10,000 dirs / 100,000 files, all four methods, once each.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\bench.ps1 -Dirs 50 -Files 500 -Methods rmdir,remove-item,robocopy
    Tiny smoke run of the harness without DeleteMan.Bench.exe.

.EXAMPLE
    pwsh -File scripts\bench.ps1 -Methods delete-man,rmdir -Repeat 3 -Work D:\scratch\dm-bench
#>
[CmdletBinding()]
param(
    [string]$Work = 'D:\projects\delete-man\testdata\bench',
    [ValidateRange(0, 10000000)][int]$Dirs = 10000,
    [ValidateRange(0, 100000000)][int]$Files = 100000,
    [string[]]$Methods = @('delete-man', 'rmdir', 'remove-item', 'robocopy'),
    [string]$BenchExe = 'D:\projects\delete-man\src\DeleteMan.Bench\bin\x64\Release\net10.0-windows\win-x64\publish\DeleteMan.Bench.exe',
    [ValidateRange(1, 1000)][int]$Repeat = 1
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$KnownMethods = @('delete-man', 'rmdir', 'remove-item', 'robocopy')
$TreeSeed = 42
$Inv = [System.Globalization.CultureInfo]::InvariantCulture

function Invoke-NativeCapture([scriptblock]$Block) {
    # Native stderr must not turn into terminating errors under 'Stop' (PS 5.1).
    $ErrorActionPreference = 'Continue'
    & $Block 2>&1 | ForEach-Object { "$_" }
}

function Test-Exists([string]$Path) {
    return ([System.IO.Directory]::Exists($Path) -or [System.IO.File]::Exists($Path))
}

function Get-RemainingCount([string]$Path) {
    if (-not [System.IO.Directory]::Exists($Path)) { return 1 }
    $count = 0
    $stack = New-Object 'System.Collections.Generic.Stack[string]'
    $stack.Push($Path)
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        try {
            foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($dir)) {
                $count++
                $attrs = [System.IO.File]::GetAttributes($entry)
                if (($attrs -band [System.IO.FileAttributes]::Directory) -and -not ($attrs -band [System.IO.FileAttributes]::ReparsePoint)) {
                    $stack.Push($entry)
                }
            }
        } catch {
            # Unreadable subtree: count what we could see.
        }
    }
    return $count
}

function Format-Num([double]$Value, [string]$Format) { return $Value.ToString($Format, $Inv) }

try {
    # ---- resolve + validate --------------------------------------------------
    $Work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Work)
    $Work = [System.IO.Path]::GetFullPath($Work).TrimEnd('\')
    $workRoot = [System.IO.Path]::GetPathRoot($Work + '\').TrimEnd('\')
    if ($Work.Length -lt 10 -or $Work -eq $workRoot) {
        throw "Refusing to run: -Work '$Work' is a drive root or shorter than 10 characters."
    }

    # '-File' passes "a,b,c" as one string, so split on commas as well.
    $methodList = New-Object 'System.Collections.Generic.List[string]'
    foreach ($m in $Methods) {
        foreach ($part in ($m -split ',')) {
            $name = $part.Trim().ToLowerInvariant()
            if ($name.Length -eq 0) { continue }
            if ($KnownMethods -notcontains $name) { throw "Unknown method '$name'. Valid: $($KnownMethods -join ', ')" }
            if (-not $methodList.Contains($name)) { $methodList.Add($name) }
        }
    }
    if ($methodList.Count -eq 0) { throw 'No methods selected.' }

    $makeTree = Join-Path $PSScriptRoot 'make-tree.ps1'
    if (-not [System.IO.File]::Exists($makeTree)) { throw "make-tree.ps1 not found next to bench.ps1 ($makeTree)." }

    [void][System.IO.Directory]::CreateDirectory($Work)
    $empty = Join-Path $Work 'empty'
    if ($methodList.Contains('robocopy')) {
        [void][System.IO.Directory]::CreateDirectory($empty)
        if (@([System.IO.Directory]::GetFileSystemEntries($empty)).Count -gt 0) {
            throw "'$empty' must be empty (robocopy /MIR would copy its contents)."
        }
    }

    Write-Host ("bench: work={0} dirs={1} files={2} methods={3} repeat={4}" -f $Work, $Dirs, $Files, ($methodList -join ','), $Repeat)

    # ---- runs -------------------------------------------------------------------
    $results = New-Object 'System.Collections.Generic.List[object]'
    $anyFailure = $false

    foreach ($method in $methodList) {
        if ($method -eq 'delete-man' -and -not [System.IO.File]::Exists($BenchExe)) {
            Write-Host ''
            Write-Host "SKIP delete-man: bench exe not found at '$BenchExe' (run scripts\publish-bench.ps1 first, or pass -BenchExe)."
            $results.Add([pscustomobject]@{ Method = $method; Run = 0; Files = $Files; Dirs = $Dirs; Seconds = -1.0; Status = 'SKIPPED' })
            continue
        }

        for ($run = 1; $run -le $Repeat; $run++) {
            $tree = Join-Path $Work ("tree-{0}-{1}" -f $method, $run)
            Write-Host ''
            Write-Host ("--- {0} #{1}: {2}" -f $method, $run, $tree)

            if (Test-Exists $tree) {
                Write-Host "FAILED: '$tree' already exists (left over from an earlier run?). Remove it and re-run."
                $results.Add([pscustomobject]@{ Method = $method; Run = $run; Files = $Files; Dirs = $Dirs; Seconds = -1.0; Status = 'FAILED (tree existed)' })
                $anyFailure = $true
                continue
            }

            & $makeTree -Root $tree -Dirs $Dirs -Files $Files -Seed $TreeSeed | Out-Host
            if ($LASTEXITCODE -ne 0) {
                Write-Host "FAILED: make-tree.ps1 exited with $LASTEXITCODE"
                $results.Add([pscustomobject]@{ Method = $method; Run = $run; Files = $Files; Dirs = $Dirs; Seconds = -1.0; Status = 'FAILED (make-tree)' })
                $anyFailure = $true
                continue
            }

            Start-Sleep -Seconds 2

            $output = @()
            $detail = ''
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            switch ($method) {
                'delete-man' {
                    $output = @(Invoke-NativeCapture { & $BenchExe $tree })
                    if ($LASTEXITCODE -ne 0) { $detail = "exit $LASTEXITCODE" }
                }
                'rmdir' {
                    $output = @(Invoke-NativeCapture { & cmd.exe /c rmdir /s /q $tree })
                    if ($LASTEXITCODE -ne 0) { $detail = "exit $LASTEXITCODE" }
                }
                'remove-item' {
                    $removeErrors = $null
                    Remove-Item -LiteralPath $tree -Recurse -Force -ErrorAction SilentlyContinue -ErrorVariable removeErrors
                    if ($removeErrors -and $removeErrors.Count -gt 0) {
                        $detail = "$($removeErrors.Count) errors"
                        $output = @($removeErrors | Select-Object -First 3 | ForEach-Object { $_.ToString() })
                    }
                }
                'robocopy' {
                    # /LOG:NUL: with /MT robocopy still prints every *EXTRA entry despite
                    # /NFL /NDL; piping ~100k lines through PowerShell would skew the timing.
                    $output = @(Invoke-NativeCapture { & robocopy.exe $empty $tree /MIR /MT:16 /NFL /NDL /NJH /NJS /NP /R:0 /W:0 /LOG:NUL } |
                            Where-Object { $_ -notmatch '^\s*Log File\s*:' })
                    $rc = $LASTEXITCODE
                    $output += @(Invoke-NativeCapture { & cmd.exe /c rmdir /q $tree })
                    $rd = $LASTEXITCODE
                    if ($rc -ge 8) { $detail = "robocopy exit $rc" }
                    elseif ($rd -ne 0) { $detail = "rmdir exit $rd" }
                }
            }
            $sw.Stop()
            $seconds = $sw.Elapsed.TotalSeconds

            foreach ($line in $output) { if ("$line".Trim().Length -gt 0) { Write-Host "  | $line" } }

            if (Test-Exists $tree) {
                $remaining = Get-RemainingCount $tree
                $status = "FAILED ($remaining entries remain)"
                if ($detail) { $status = "FAILED ($remaining entries remain; $detail)" }
                Write-Host ("{0} after {1} s - tree left in place for inspection: {2}" -f $status, (Format-Num $seconds '0.000'), $tree)
                $anyFailure = $true
            } else {
                $status = 'OK'
                if ($detail) { $status = "OK ($detail)" }
                Write-Host ("{0} in {1} s" -f $status, (Format-Num $seconds '0.000'))
            }
            $results.Add([pscustomobject]@{ Method = $method; Run = $run; Files = $Files; Dirs = $Dirs; Seconds = $seconds; Status = $status })
        }
    }

    if ([System.IO.Directory]::Exists($empty) -and @([System.IO.Directory]::GetFileSystemEntries($empty)).Count -eq 0) {
        [System.IO.Directory]::Delete($empty)
    }

    # ---- report -----------------------------------------------------------------
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $lines.Add('| Method | Files | Dirs | Seconds | Files/s | OK |')
    $lines.Add('|---|---:|---:|---:|---:|---|')
    foreach ($r in $results) {
        $label = $r.Method
        if ($Repeat -gt 1 -and $r.Run -gt 0) { $label = "$($r.Method) #$($r.Run)" }
        if ($r.Seconds -lt 0) { $secText = '-'; $rateText = '-' }
        else {
            $secText = Format-Num $r.Seconds '0.000'
            $rateText = '-'
            if ($r.Seconds -gt 0) { $rateText = Format-Num ($r.Files / $r.Seconds) '0' }
        }
        $lines.Add(('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $label, $r.Files, $r.Dirs, $secText, $rateText, $r.Status))
    }
    $lines.Add('')

    # Best (minimum) time per method among successful runs.
    $best = [ordered]@{}
    foreach ($r in $results) {
        if ($r.Status -like 'OK*' -and $r.Seconds -ge 0) {
            if (-not $best.Contains($r.Method) -or $r.Seconds -lt $best[$r.Method]) { $best[$r.Method] = $r.Seconds }
        }
    }
    if ($best.Count -gt 0) {
        $fastest = $null
        foreach ($k in $best.Keys) { if ($null -eq $fastest -or $best[$k] -lt $best[$fastest]) { $fastest = $k } }
        $lines.Add(('Fastest: **{0}** ({1} s best run)' -f $fastest, (Format-Num $best[$fastest] '0.000')))
    } else {
        $lines.Add('Fastest: n/a (no successful runs)')
    }
    $lines.Add('')
    if ($best.Contains('delete-man')) {
        $dm = $best['delete-man']
        foreach ($k in $best.Keys) {
            if ($k -eq 'delete-man') { continue }
            if ($dm -gt 0) {
                $ratio = $best[$k] / $dm
                $word = 'faster'
                if ($ratio -lt 1) { $word = 'slower' }
                $lines.Add(('- delete-man vs {0}: {1}x ({2})' -f $k, (Format-Num $ratio '0.00'), $word))
            }
        }
    } else {
        $lines.Add('- delete-man speedup: n/a (delete-man not selected, skipped or failed)')
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $resultFile = Join-Path $Work ("results-{0}.md" -f $timestamp)
    $header = @(
        "# Delete Man deletion benchmark $timestamp",
        '',
        ('Tree: {0} dirs, {1} files, seed {2}; repeat {3}; host {4}; PowerShell {5}' -f $Dirs, $Files, $TreeSeed, $Repeat, $env:COMPUTERNAME, $PSVersionTable.PSVersion),
        ''
    )
    $report = ($header + $lines) -join "`r`n"
    [System.IO.File]::WriteAllText($resultFile, $report + "`r`n", (New-Object System.Text.UTF8Encoding($false)))

    Write-Host ''
    foreach ($l in $lines) { Write-Host $l }
    Write-Host ''
    Write-Host "Results written to $resultFile"

    if ($anyFailure) { exit 1 }
    exit 0
} catch {
    [Console]::Error.WriteLine('bench: ERROR: ' + $_.Exception.Message)
    exit 1
}
