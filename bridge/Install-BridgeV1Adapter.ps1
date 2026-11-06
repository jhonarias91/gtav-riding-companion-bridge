# Installs the precompiled adapter from an integration package. Run with GTA V closed.
[CmdletBinding(SupportsShouldProcess = $true)]
param([Parameter(Mandatory = $true)][string]$GtaDirectory)
$ErrorActionPreference = 'Stop'
$gta = (Resolve-Path -LiteralPath $GtaDirectory).Path
$scripts = (Resolve-Path -LiteralPath (Join-Path $gta 'scripts')).Path
if (Get-Process -Name 'GTA5', 'GTA5_Enhanced' -ErrorAction SilentlyContinue) { throw 'Close GTA V before installing.' }
$name = 'GTBikeVCompanionV1.dll'
$source = Join-Path $PSScriptRoot $name
$target = Join-Path $scripts $name
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json
$hook = Join-Path $gta 'ScriptHookVDotNet3.dll'
$mod = Join-Path $scripts 'GTBikeV.dll'
if ((Get-FileHash -LiteralPath $hook).Hash -ne $manifest.scriptHookVDotNetReferenceSha256) {
    throw 'ScriptHookVDotNet3.dll differs from the one this package was built against.'
}
if ([Reflection.AssemblyName]::GetAssemblyName($mod).Version.ToString() -ne $manifest.gtbikevAssemblyVersion) {
    throw 'The GTBikeV.dll version does not match the package profile.'
}
$entry = @($manifest.files | Where-Object { $_.name -ceq $name })
if ($entry.Count -ne 1 -or (Get-FileHash -LiteralPath $source).Hash -ne $entry[0].sha256) {
    throw 'The adapter does not match the SHA-256 in the package manifest.'
}
if (-not $PSCmdlet.ShouldProcess($target, 'Install the v1 adapter')) { return }

$transaction = [Guid]::NewGuid().ToString('N')
$temp = "$target.$transaction.tmp"
try {
    Copy-Item -LiteralPath $source -Destination $temp
    if ((Get-FileHash -LiteralPath $temp).Hash -ne $entry[0].sha256) { throw 'The copied file does not match the package adapter.' }
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        $backup = "$target.$((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff')).$transaction.bak"
        [IO.File]::Replace($temp, $target, $backup)
        Write-Host "Previous adapter backed up to: $backup"
    } else {
        [IO.File]::Move($temp, $target)
    }
    Write-Host "Adapter installed: $target"
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
}
