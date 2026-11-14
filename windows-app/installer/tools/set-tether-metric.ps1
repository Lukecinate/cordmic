<#
  CordMic: give the phone's USB-tethering adapter the lowest routing priority (interface metric 9999)
  so games and Windows keep using LAN/Wi-Fi for internet while the phone only carries mic audio.
  Since 0.6.1 it also keeps the firewall rule narrow: the tether network is set to the Private profile and the
  rule "CordMic (UDP 50005)" allows only the tether subnet (Private profile). With no tether adapter the rule is
  scoped to 127.0.0.1, which admits nothing from the network.
  Since 0.7.2 the phone link is "mic only": the tether interface ignores default routes (IPv4 and IPv6), any
  default route through the phone is removed and the interface's DNS server is set to loopback, so Windows never
  sends internet traffic (and never spends mobile data) through the phone. Opt out: an administrator creates
  %ProgramData%\CordMic\allow-phone-internet, and the next run restores DHCP DNS and default routes.
  Runs as SYSTEM from the scheduled task "\CordMic\SetTetherMetric" (network connected/disconnected events,
  logon, boot) or elevated from the tray menu ("Perbaiki prioritas jaringan HP").
#>
param(
    [int]$WaitSeconds = 30,   # keep looking this long: the adapter may still be getting its DHCP lease
    [switch]$Once
)
$ErrorActionPreference = 'Continue'
$pattern = 'Remote NDIS|RNDIS|UsbNcm|USB NCM|\bNCM\b|SAMSUNG Mobile USB|Android USB Ethernet'
$metric  = 9999
$ruleName = 'CordMic (UDP 50005)'
$logDir  = Join-Path $env:ProgramData 'CordMic'
$log     = Join-Path $logDir 'tether-metric.log'
$allowInternet = Test-Path (Join-Path $logDir 'allow-phone-internet')
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
function Log($m) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m
    try {
        if ((Test-Path $log) -and (Get-Item $log).Length -gt 512KB) { Move-Item $log "$log.1" -Force }
        Add-Content -Path $log -Value $line
    } catch { }
}

# IPv4 network address in CIDR form, e.g. 192.168.42.129/24 -> 192.168.42.0/24.
function Get-CordMicSubnet([string]$Ip, [int]$Prefix) {
    $b = [System.Net.IPAddress]::Parse($Ip).GetAddressBytes()
    [Array]::Reverse($b)
    $v = [uint64][BitConverter]::ToUInt32($b, 0)
    $all = [uint64]4294967295
    $mask = if ($Prefix -le 0) { [uint64]0 } else { ($all -shl (32 - [Math]::Min($Prefix, 32))) -band $all }
    $nb = [BitConverter]::GetBytes([uint32]($v -band $mask))
    [Array]::Reverse($nb)
    return "{0}/{1}" -f ([System.Net.IPAddress]::new($nb)), $Prefix
}

# 0.7.2: mic-only link. Never route internet (or DNS) through the phone; idempotent, logs only changes.
function Set-CordMicMicOnly($a) {
    foreach ($fam in 'IPv4', 'IPv6') {
        $ipif = Get-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily $fam -ErrorAction SilentlyContinue
        if (-not $ipif) { continue }
        $want = if ($allowInternet) { 'Disabled' } else { 'Enabled' }
        if ("$($ipif.IgnoreDefaultRoutes)" -ne $want) {
            try {
                Set-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily $fam -IgnoreDefaultRoutes $want -ErrorAction Stop
                Log "$fam ignore-default-routes $want on '$($a.Name)'"
            } catch { Log "could not set $fam ignore-default-routes on '$($a.Name)': $($_.Exception.Message)" }
        }
    }
    if ($allowInternet) {
        $dns = Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
        if ($dns -and (@($dns.ServerAddresses) -contains '127.0.0.1')) {
            try { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction Stop; Log "DNS reset to DHCP on '$($a.Name)' (allow-phone-internet)" }
            catch { Log "DNS reset failed on '$($a.Name)': $($_.Exception.Message)" }
        }
        return
    }
    foreach ($r in @(Get-NetRoute -InterfaceIndex $a.ifIndex -DestinationPrefix '0.0.0.0/0', '::/0' -ErrorAction SilentlyContinue)) {
        try {
            Remove-NetRoute -InputObject $r -Confirm:$false -ErrorAction Stop
            Log "removed default route ($($r.AddressFamily)) via the phone on '$($a.Name)'"   # next hop not logged
        } catch { Log "could not remove default route on '$($a.Name)': $($_.Exception.Message)" }
    }
    $dns4 = Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
    if ($dns4 -and -not ((@($dns4.ServerAddresses).Count -eq 1) -and (@($dns4.ServerAddresses)[0] -eq '127.0.0.1'))) {
        try {
            Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses '127.0.0.1' -ErrorAction Stop
            Log "DNS on '$($a.Name)' set to loopback (no lookups through the phone)"
        } catch { Log "could not set DNS on '$($a.Name)': $($_.Exception.Message)" }
    }
}

# Private profile + tether subnet only. If Windows refuses to mark the tether network Private, the rule also
# covers the Public profile, but still only for the tether subnet.
function Update-CordMicFirewall($Adapters) {
    $rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if (-not $rule) { return }
    $subnets = @()
    $allPrivate = $true
    foreach ($a in @($Adapters)) {
        $ips = Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike '169.254.*' }
        foreach ($ip in @($ips)) { $subnets += Get-CordMicSubnet $ip.IPAddress $ip.PrefixLength }
        $prof = Get-NetConnectionProfile -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue
        if ($prof -and "$($prof.NetworkCategory)" -eq 'Public') {
            try {
                Set-NetConnectionProfile -InterfaceIndex $a.ifIndex -NetworkCategory Private -ErrorAction Stop
                Log "set network category Private on '$($a.Name)'"
            } catch { $allPrivate = $false; Log "could not set Private on '$($a.Name)': $($_.Exception.Message)" }
        } elseif (-not $prof) { $allPrivate = $false }
    }
    $subnets = @($subnets | Select-Object -Unique)
    try {
        if ($subnets.Count -eq 0) {
            Set-NetFirewallRule -DisplayName $ruleName -RemoteAddress 127.0.0.1 -Profile Private -Enabled True -ErrorAction Stop
            Log "firewall: no tether adapter, rule scoped to 127.0.0.1"
        } else {
            $prof = if ($allPrivate) { 'Private' } else { 'Private, Public' }
            Set-NetFirewallRule -DisplayName $ruleName -RemoteAddress $subnets -Profile $prof -Enabled True -ErrorAction Stop
            Log "firewall: rule scoped to $($subnets -join ', ') (profile $prof)"
        }
    } catch { Log "firewall update failed: $($_.Exception.Message)" }
}

if ($Once) { $WaitSeconds = 5 }
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$changed = 0
do {
    $adapters = Get-NetAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.InterfaceDescription -match $pattern -or $_.Name -match $pattern }
    foreach ($a in $adapters) {
        Set-CordMicMicOnly $a
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
Update-CordMicFirewall $adapters
