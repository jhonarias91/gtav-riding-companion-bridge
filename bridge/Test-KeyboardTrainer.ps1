param([string]$ReferenceRoot = (Join-Path $PSScriptRoot '..\..\reference\GTBikeV-0.8.0.4-decompiled\GTBikeV'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot '..\outputs\keyboard-trainer-test'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output 'KeyboardTrainerHarness.exe'
$physics = Join-Path $ReferenceRoot 'VirtualTrainer.cs'
if (-not (Test-Path -LiteralPath $physics)) { throw 'The static GTBikeV 0.8.0.4 reference is needed for the native physics regression test. Pass -ReferenceRoot.' }
& $compiler /nologo /warnaserror+ /target:exe /platform:x64 "/out:$exe" `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1KeyboardTrainer.cs') `
    (Join-Path $PSScriptRoot '..\tests\KeyboardTrainerHarness.cs') $physics
if ($LASTEXITCODE -ne 0) { throw 'Keyboard trainer harness compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Keyboard trainer harness failed.' }
