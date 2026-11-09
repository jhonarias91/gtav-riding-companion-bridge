$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot '..\outputs\camera-test'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output 'CameraHarness.exe'
& $compiler /nologo /warnaserror+ /target:exe /platform:x64 "/out:$exe" (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Camera.cs') (Join-Path $PSScriptRoot '..\tests\CameraHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Camera harness compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Camera harness failed.' }
