<#
  CordMic machine-wide setup, called (elevated) by the NSIS installer/uninstaller.
    -Action Install   : firewall rule for UDP 50005 + scheduled task that de-prioritises the tether adapter
    -Action Uninstall : removes both
#>
param(
    [Parameter(Mandatory)][ValidateSet('Install', 'Uninstall')][string]$Action,
    [string]$InstallDir = $PSScriptRoot,
    [int]$Port = 50005
)
$ErrorActionPreference = 'Continue'
$ruleName = 'CordMic (UDP 50005)'
$taskPath = '\CordMic\'
$taskName = 'SetTetherMetric'
$logDir   = Join-Path $env:ProgramData 'CordMic'
# 0.6.1 (audit L6): never reuse a junction/symlink; Administrators own the folder, only Administrators and SYSTEM
# can write, Users can read (logs). The elevated VB-CABLE helper and the SYSTEM task write here.
$existing = Get-Item -LiteralPath $logDir -Force -ErrorAction SilentlyContinue
if ($existing -and ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    Remove-Item -LiteralPath $logDir -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
& "$env:SystemRoot\System32\icacls.exe" $logDir /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
& "$env:SystemRoot\System32\icacls.exe" $logDir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /T /C /Q | Out-Null
Start-Transcript -Path (Join-Path $logDir 'setup.log') -Append | Out-Null

# Pre-0.3.0 product name: its rule/task are removed too, so upgrades leave nothing behind.
$legacyRule     = 'UsbMic (UDP 50005)'
$legacyTaskPath = '\UsbMic\'

function Remove-All {
    foreach ($r in @($ruleName, $legacyRule)) {
        Get-NetFirewallRule -DisplayName $r -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    }
    foreach ($p in @($taskPath, $legacyTaskPath)) {
        Unregister-ScheduledTask -TaskPath $p -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}

# 0.7.2: undo the mic-only settings on tethering adapters Windows still knows (DNS back to DHCP, default routes allowed).
function Reset-TetherAdapters {
    $pattern = 'Remote NDIS|RNDIS|UsbNcm|USB NCM|\bNCM\b|SAMSUNG Mobile USB|Android USB Ethernet'
    foreach ($a in @(Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceDescription -match $pattern -or $_.Name -match $pattern })) {
        foreach ($fam in 'IPv4', 'IPv6') { Set-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily $fam -IgnoreDefaultRoutes Disabled -ErrorAction SilentlyContinue }
        Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction SilentlyContinue
    }
}

if ($Action -eq 'Uninstall') {
    Reset-TetherAdapters
    Remove-All
    Write-Output 'CordMic: firewall rule and task removed'
    Stop-Transcript | Out-Null
    exit 0
}

Remove-All
$exe = Join-Path (Split-Path $InstallDir -Parent) 'CordMic.exe'
if (-not (Test-Path $exe)) { $exe = Join-Path $InstallDir 'CordMic.exe' }

# 0.6.1 (audit H1): inbound UDP from the phone only. The rule starts scoped to 127.0.0.1 (nothing from the network);
# the SYSTEM task below sets the tether network to Private and scopes the rule to the tether subnet whenever the
# phone connects. Upgrades remove the old LocalSubnet/Any rule above and create this one.
New-NetFirewallRule -DisplayName $ruleName -Description 'CordMic: audio from the phone over USB tethering (tether subnet only)' `
    -Direction Inbound -Action Allow -Protocol UDP -LocalPort $Port -Program $exe `
    -RemoteAddress 127.0.0.1 -Profile Private | Out-Null
Write-Output "firewall rule added for $exe"

$script = Join-Path $InstallDir 'set-tether-metric.ps1'
$taskArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
$sub = '&lt;QueryList&gt;&lt;Query Id="0" Path="Microsoft-Windows-NetworkProfile/Operational"&gt;&lt;Select Path="Microsoft-Windows-NetworkProfile/Operational"&gt;*[System[(EventID=10000 or EventID=10001)]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;'
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>CordMic: beri prioritas rendah ke adapter USB tethering HP agar internet tetap lewat LAN/Wi-Fi.</Description>
  </RegistrationInfo>
  <Triggers>
    <EventTrigger><Enabled>true</Enabled><Subscription>$sub</Subscription></EventTrigger>
    <BootTrigger><Enabled>true</Enabled></BootTrigger>
    <LogonTrigger><Enabled>true</Enabled></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
    <Priority>7</Priority>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>powershell.exe</Command><Arguments>$([System.Security.SecurityElement]::Escape($taskArgs))</Arguments></Exec>
  </Actions>
</Task>
"@
Register-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Xml $xml -Force | Out-Null
Write-Output "scheduled task $taskPath$taskName registered"
# Apply once now in case the phone is already plugged in.
Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue
Stop-Transcript | Out-Null
exit 0
