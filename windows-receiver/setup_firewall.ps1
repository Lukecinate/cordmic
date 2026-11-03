# Jalankan di PowerShell sebagai Administrator
New-NetFirewallRule -DisplayName "CordMic UDP 50005" -Direction Inbound -Protocol UDP -LocalPort 50005 -Action Allow -Profile Any
