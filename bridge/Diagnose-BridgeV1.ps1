# Local read-only diagnostic. Prints selected compatibility fields and never dumps the adapter snapshot.
param([ValidateRange(1024,65535)][int]$Port = 8788)
$ErrorActionPreference = 'Stop'
$node = Get-Command node -ErrorAction SilentlyContinue
$state = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'GTBikeVCompanion\v1\telemetry.json'
$report = [ordered]@{
    Node = if ($node) { & $node.Source --version } else { 'missing' }
    Port = $Port
    Listening = [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    Snapshot = 'absent'
    SnapshotAgeSeconds = $null
    GTBikeV = $null
    Binding = $null
    GameControl = $null
    Mod = $null
}
try {
    $info = Get-Item -LiteralPath $state -ErrorAction SilentlyContinue
    if ($info -and $info.Length -le 262144) {
        $snapshot = Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
        if ($snapshot.schema -ne 'gtbikev-adapter-v1') { throw 'Schema' }
        $age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$snapshot.capturedAtUtc).TotalSeconds
        $report.Snapshot = if ($age -ge 0 -and $age -le 5) { 'recent' } else { 'stale' }
        $report.SnapshotAgeSeconds = [Math]::Round($age, 2)
        if ($snapshot.modVersion -match '^\d+(\.\d+){3}$') { $report.GTBikeV = $snapshot.modVersion }
        if ($snapshot.binding -in @('compatible', 'limited', 'incompatible', 'unknown')) { $report.Binding = $snapshot.binding }
        if ($snapshot.gameControl -in @('inControl', 'paused', 'loading', 'notInControl', 'unknown')) { $report.GameControl = $snapshot.gameControl }
        if ($snapshot.modLayer -in @('inactive', 'initializing', 'dataReady', 'stopping', 'error', 'unknown')) { $report.Mod = $snapshot.modLayer }
    } elseif ($info) {
        $report.Snapshot = 'invalid'
    }
} catch {
    $report.Snapshot = 'invalid'
}
[pscustomobject]$report
