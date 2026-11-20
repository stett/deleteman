<#
.SYNOPSIS
    Sets the product version in Directory.Build.props, the single source of truth for the version.

.DESCRIPTION
    Replaces the value of the <Version> element in Directory.Build.props with <x.y.z>, keeping the file's
    encoding and line endings, and prints "old -> new". Every project in the solution inherits that version:
    the UI badge, the executables' file properties, the portable zip name and the installer name all follow it.
    Replaces `npm version` + scripts/sync-version.mjs from the Rust/Tauri build.

    Works on Windows PowerShell 5.1 and PowerShell 7+.
    Exit codes: 0 on success (including "already <x.y.z>"), 1 on any failure.

.PARAMETER Version
    The new version, of the form x.y.z (digits only, for example 0.2.1).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\bump-version.ps1 0.2.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Version
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2

try {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "version '$Version' is not of the form x.y.z (for example 0.2.1)"
    }

    $root = Split-Path -Parent $PSScriptRoot
    $props = Join-Path $root "Directory.Build.props"
    if (-not (Test-Path $props)) { throw "$props not found" }

    # Read and write through .NET with an explicit encoding so line endings and the BOM (or its absence)
    # survive untouched; only the characters of the version value change.
    $bytes = [System.IO.File]::ReadAllBytes($props)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $utf8 = New-Object System.Text.UTF8Encoding $hasBom
    $text = [System.IO.File]::ReadAllText($props, $utf8)

    $m = [regex]::Match($text, '<Version>\s*([^<]*?)\s*</Version>')
    if (-not $m.Success) { throw "Directory.Build.props: no <Version> element found" }

    $old = $m.Groups[1].Value
    if ($old -eq $Version) {
        Write-Host "Directory.Build.props: already $Version"
        exit 0
    }

    $g = $m.Groups[1]
    $text = $text.Substring(0, $g.Index) + $Version + $text.Substring($g.Index + $g.Length)
    [System.IO.File]::WriteAllText($props, $text, $utf8)
    Write-Host "Directory.Build.props: $old -> $Version"
    exit 0
}
catch {
    Write-Host "bump-version: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
