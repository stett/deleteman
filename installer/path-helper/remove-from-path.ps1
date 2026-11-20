# Removes a directory from the current user's PATH. Run by the uninstaller of the NSIS installer
# (installer\deleteman.nsi, before the files are removed) with the install directory as the argument.
# Preserves REG_EXPAND_SZ and broadcasts the change.
param([Parameter(Mandatory = $true)][string]$Dir)
$ErrorActionPreference = 'Stop'

$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
if (-not $key) { return }
try {
    $raw = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $parts = @($raw -split ';' | Where-Object { $_ -ne '' -and $_ -ne $Dir })
    $key.SetValue('Path', ($parts -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
    Write-Output "Removed from PATH: $Dir"
} finally {
    $key.Close()
}

Add-Type -Namespace Win32 -Name Env -MemberDefinition '[DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Auto)] public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out System.UIntPtr lpdwResult);'
$res = [System.UIntPtr]::Zero
[void][Win32.Env]::SendMessageTimeout([System.IntPtr]0xffff, 0x1A, [System.UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$res)
