# Resolves the v1 adapter bindings (P2 + P3/P4) against the real GTBikeV.dll by reflection only. No GTA V running.
# Default paths: the extracted 0.8.0.4 package and the GTA V folder with ScriptHookVDotNet3.
param(
    [string]$ModDll = (Join-Path $PSScriptRoot '..\..\GTBikeV_0_8_0_4\Scripts\GTBikeV.dll'),
    [string]$GtaDirectory = 'G:\SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced'
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$mod = (Resolve-Path -LiteralPath $ModDll).Path
$outputDirectory = Join-Path $PSScriptRoot '..\outputs\binding-resolve'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$exe = Join-Path $outputDirectory 'BindingResolveCheck.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$exe" `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Binding.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Extensions.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1More.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1KeyboardTrainer.cs') `
    (Join-Path $PSScriptRoot '..\tests\BindingResolveCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'The binding check failed to build.' }
& $exe $mod (Split-Path $mod) $GtaDirectory (Join-Path $GtaDirectory 'scripts')
if ($LASTEXITCODE -ne 0) { throw 'Some bindings did not resolve against GTBikeV.dll.' }
