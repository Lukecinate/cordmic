# Read-only record of Secure Boot and Memory Integrity (HVCI) for the M1 report.
# This script never changes those settings and never installs a driver.
# Run in PowerShell on the user's PC. Mask any user names that appear in paths
# before sending the output.

$ErrorActionPreference = 'Continue'
Write-Output 'gate=PENDING-HARDWARE'
Write-Output 'script=record-windows-security.ps1'
Write-Output ('os=' + [Environment]::OSVersion.VersionString)

function Get-SecureBootState {
    try {
        $v = Confirm-SecureBootUEFI
        if ($v -eq $true) { return 'on' }
        if ($v -eq $false) { return 'off' }
        return 'unknown'
    } catch {
        return 'unavailable'
    }
}

function Get-MemoryIntegrityState {
    $path = 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity'
    try {
        if (Test-Path $path) {
            $en = (Get-ItemProperty -Path $path -Name Enabled -ErrorAction Stop).Enabled
            if ($en -eq 1) { return 'on' }
            if ($en -eq 0) { return 'off' }
        }
        return 'unknown'
    } catch {
        return 'unavailable'
    }
}

Write-Output ('secure_boot=' + (Get-SecureBootState))
Write-Output ('memory_integrity=' + (Get-MemoryIntegrityState))
Write-Output 'note=Leave Secure Boot and Memory Integrity as they are. CordMic will not ask you to turn them off.'
Write-Output 'note=Samsung Auto Blocker is noted by hand: Settings > Security and privacy > Auto Blocker (on/off). Do not change it for CordMic.'
