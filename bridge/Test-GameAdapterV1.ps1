# Compiles the v1 binding against a mock GTBikeV surface and runs it. No GTA V or ScriptHookVDotNet needed.
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '64-bit .NET Framework 4.8 (csc.exe) is required.' }
$outputDirectory = Join-Path $PSScriptRoot '..\outputs\game-adapter-v1-test'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$exe = Join-Path $outputDirectory 'GameAdapterV1Harness.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$exe" `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Binding.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Extensions.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1More.cs') `
    (Join-Path $PSScriptRoot '..\tests\GameAdapterV1Harness.cs')
if ($LASTEXITCODE -ne 0) { throw 'The v1 adapter harness failed to build.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'The v1 adapter harness failed.' }
