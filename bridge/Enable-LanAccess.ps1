param([ValidateRange(1024,65535)][int]$Port=8788,[switch]$Remove)
$ErrorActionPreference='Stop'
$ruleName="GTBikeVCompanion-LAN-$Port"
if($Remove){Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule;Write-Host 'Bridge firewall rule removed.';exit}
$nodeExecutable=(Get-Command node -ErrorAction Stop).Source
if(Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue){Write-Host 'The bridge firewall rule already exists.';exit}
New-NetFirewallRule -Name $ruleName -DisplayName "GTAV Riding Companion Wi-Fi ($Port)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Program $nodeExecutable -Profile Private -RemoteAddress LocalSubnet | Out-Null
Write-Host "Firewall rule created: Node.js on TCP $Port, Private profile, local subnet only."
