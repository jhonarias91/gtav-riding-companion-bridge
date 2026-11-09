# Runs the real add-on lifecycle against fake game surfaces. No GTA V process is used.
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputDirectory = Join-Path $PSScriptRoot '..\outputs\addon-switch-test'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$exe = Join-Path $outputDirectory 'AddonSwitchHarness.exe'
& $compiler /nologo /target:exe /platform:x64 /reference:System.Web.Extensions.dll "/out:$exe" `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Addons.cs') `
    (Join-Path $PSScriptRoot '..\tests\AddonSwitchHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'The add-on switch harness failed to build.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'The add-on switch harness failed.' }
