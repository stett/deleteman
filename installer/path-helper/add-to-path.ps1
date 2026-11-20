# Adds a directory to the current user's PATH so the app is launchable from any shell.
# Run by the NSIS installer (installer\deleteman.nsi, after the files are copied) with the install directory
# as the argument. Idempotent, preserves REG_EXPAND_SZ and existing %VAR% entries, and broadcasts the change so
# newly started shells pick it up without a sign-out.
param([Parameter(Mandatory = $true)][string]$Dir)
$ErrorActionPreference = 'Stop'

$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
if (-not $key) { $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment') }
try {
    $raw = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $parts = @($raw -split ';' | Where-Object { $_ -ne '' })
    if ($parts -notcontains $Dir) {
        $parts += $Dir
        $key.SetValue('Path', ($parts -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
        Write-Output "Added to PATH: $Dir"
    } else {
        Write-Output "Already on PATH: $Dir"
    }
} finally {
    $key.Close()
}

# Tell running processes the environment changed, so new shells see the updated PATH.
Add-Type -Namespace Win32 -Name Env -MemberDefinition '[DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Auto)] public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out System.UIntPtr lpdwResult);'
$res = [System.UIntPtr]::Zero
[void][Win32.Env]::SendMessageTimeout([System.IntPtr]0xffff, 0x1A, [System.UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$res)
