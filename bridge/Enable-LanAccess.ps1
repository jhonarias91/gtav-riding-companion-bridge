param([ValidateRange(1024,65535)][int]$Port=8788,[switch]$Remove,[string]$NodeExecutable)
$ErrorActionPreference='Stop'
$ruleName="GTBikeVCompanion-LAN-$Port"
if($Remove){Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule;Write-Host 'Bridge firewall rule removed.';exit}
if(-not $NodeExecutable){$NodeExecutable=(Get-Command node -ErrorAction Stop).Source}
$NodeExecutable=(Resolve-Path -LiteralPath $NodeExecutable -ErrorAction Stop).Path
$existing=Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
if($existing){
    $program=($existing | Get-NetFirewallApplicationFilter).Program
    if($program -eq $NodeExecutable){Write-Host 'The bridge firewall rule already exists.';exit}
    $existing | Remove-NetFirewallRule
}
New-NetFirewallRule -Name $ruleName -DisplayName "GTAV Riding Companion Wi-Fi ($Port)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Program $NodeExecutable -Profile Private -RemoteAddress LocalSubnet | Out-Null
Write-Host "Firewall rule created: Node.js on TCP $Port, Private profile, local subnet only."
