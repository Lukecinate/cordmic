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
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
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

if ($Action -eq 'Uninstall') {
    Remove-All
    Write-Output 'CordMic: firewall rule and task removed'
    Stop-Transcript | Out-Null
    exit 0
}

Remove-All
$exe = Join-Path (Split-Path $InstallDir -Parent) 'CordMic.exe'
if (-not (Test-Path $exe)) { $exe = Join-Path $InstallDir 'CordMic.exe' }

# Inbound UDP from the phone only (local subnet), any profile: Windows usually classifies the tether network as Public.
New-NetFirewallRule -DisplayName $ruleName -Description 'CordMic: audio dari HP lewat USB tethering' `
    -Direction Inbound -Action Allow -Protocol UDP -LocalPort $Port -Program $exe `
    -RemoteAddress LocalSubnet -Profile Any | Out-Null
Write-Output "firewall rule added for $exe"

$script = Join-Path $InstallDir 'set-tether-metric.ps1'
$taskArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
$sub = '&lt;QueryList&gt;&lt;Query Id="0" Path="Microsoft-Windows-NetworkProfile/Operational"&gt;&lt;Select Path="Microsoft-Windows-NetworkProfile/Operational"&gt;*[System[(EventID=10000)]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;'
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
