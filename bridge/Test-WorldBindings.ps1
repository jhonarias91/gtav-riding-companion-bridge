# Resolves only the optional P8 bindings, without invoking their getters or starting GTA V.
param(
    [Parameter(Mandatory = $true)][string]$ModDll,
    [Parameter(Mandatory = $true)][string]$GtaDirectory
)
$ErrorActionPreference = 'Stop'
$mod = (Resolve-Path -LiteralPath $ModDll).Path
$gta = (Resolve-Path -LiteralPath $GtaDirectory).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot '..\outputs\world-bindings'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$probe = Join-Path $output 'WorldBindingCheck.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$probe" `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1World.cs') `
    (Join-Path $PSScriptRoot '..\tests\WorldBindingCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'The P8 binding check failed to build.' }
& $probe $mod $gta
if ($LASTEXITCODE -ne 0) { throw 'Not all P8 bindings resolved.' }
