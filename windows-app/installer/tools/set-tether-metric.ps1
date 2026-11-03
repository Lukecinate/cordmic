<#
  CordMic: give the phone's USB-tethering adapter the lowest routing priority (interface metric 9999)
  so games and Windows keep using LAN/Wi-Fi for internet while the phone only carries mic audio.
  Runs as SYSTEM from the scheduled task "\CordMic\SetTetherMetric" (network-connected event, logon, boot)
  or elevated from the tray menu ("Perbaiki prioritas jaringan HP").
#>
param(
    [int]$WaitSeconds = 30,   # keep looking this long: the adapter may still be getting its DHCP lease
    [switch]$Once
)
$ErrorActionPreference = 'Continue'
$pattern = 'Remote NDIS|RNDIS|UsbNcm|USB NCM|\bNCM\b|SAMSUNG Mobile USB|Android USB Ethernet'
$metric  = 9999
$logDir  = Join-Path $env:ProgramData 'CordMic'
$log     = Join-Path $logDir 'tether-metric.log'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
function Log($m) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m
    try {
        if ((Test-Path $log) -and (Get-Item $log).Length -gt 512KB) { Move-Item $log "$log.1" -Force }
        Add-Content -Path $log -Value $line
    } catch { }
}

if ($Once) { $WaitSeconds = 5 }
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$changed = 0
do {
    $adapters = Get-NetAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.InterfaceDescription -match $pattern -or $_.Name -match $pattern }
    foreach ($a in $adapters) {
        foreach ($fam in 'IPv4', 'IPv6') {
            $ipif = Get-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily $fam -ErrorAction SilentlyContinue
            if ($ipif -and $ipif.InterfaceMetric -ne $metric) {
                try {
                    Set-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily $fam -InterfaceMetric $metric -ErrorAction Stop
                    Log "set $fam metric $metric on '$($a.Name)' ($($a.InterfaceDescription)) was $($ipif.InterfaceMetric)"
                    $changed++
                } catch { Log "failed on '$($a.Name)' ${fam}: $($_.Exception.Message)" }
            }
        }
    }
    if ((Get-Date) -ge $deadline) { break }
    Start-Sleep -Seconds 2
} while ($true)
if ($changed -eq 0) { Log "no change needed (adapters found: $(@($adapters).Count))" }
