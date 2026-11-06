# Produces a Windows integration package. Install and acceptance checks are separate steps.
param([Parameter(Mandatory = $true)][string]$GtaDirectory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$gta = (Resolve-Path -LiteralPath $GtaDirectory).Path
$commit = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'A Git checkout is required.' }
$pending = & git -C $root status --porcelain
if ($LASTEXITCODE -ne 0 -or $pending) { throw 'The package needs a clean checkout so it can record its source commit.' }
$esbuild = Join-Path $root 'node_modules\.bin\esbuild.cmd'
if (-not (Test-Path -LiteralPath $esbuild -PathType Leaf)) { throw 'Run npm ci in the checkout first.' }
$hook = Join-Path $gta 'ScriptHookVDotNet3.dll'
if (-not (Test-Path -LiteralPath $hook -PathType Leaf)) { throw "Missing $hook." }

& (Join-Path $PSScriptRoot 'Build-GameAdapterV1.ps1') -GtaDirectory $gta
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff')
$name = "GTBikeV-BridgeV1-integration-$($commit.Substring(0, 12))-$stamp"
$destination = Join-Path $root 'outputs\release'
$stage = Join-Path $destination $name
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$entry = Join-Path $PSScriptRoot 'start-v1.mjs'
$bundle = Join-Path $stage 'bridge-v1.mjs'
& $esbuild $entry --bundle --platform=node --format=esm --target=node22 "--outfile=$bundle"
if ($LASTEXITCODE -ne 0) { throw 'The v1 bridge failed to bundle.' }
& node --check $bundle
if ($LASTEXITCODE -ne 0) { throw 'The bundled bridge failed the syntax check.' }

$adapter = Join-Path $root 'outputs\game-adapter-v1\GTBikeVCompanionV1.dll'
Copy-Item -LiteralPath $adapter -Destination $stage
foreach ($file in 'Enable-LanAccess.ps1', 'Diagnose-BridgeV1.ps1', 'Install-BridgeV1Adapter.ps1', 'ANDROID_BRIDGE_V1.md', 'ANDROID_BRIDGE_V1_ES.md') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'contracts\v1') -Destination (Join-Path $stage 'contracts') -Recurse
# The bundle includes the Garmin FIT SDK: ship its license with it.
Copy-Item -LiteralPath (Join-Path $root 'node_modules\@garmin\fitsdk\LICENSE.txt') -Destination (Join-Path $stage 'GARMIN-FIT-SDK-LICENSE.txt')
# It also includes qrcode-generator (MIT). The package ships no LICENSE file and the bundle drops its header, so
# write the notice here.
@'
qrcode-generator (https://github.com/kazuhikoarase/qrcode-generator), bundled in bridge-v1.mjs.

Copyright (c) 2009 Kazuhiko Arase

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The word "QR Code" is a registered trademark of DENSO WAVE INCORPORATED.
'@ | Set-Content -LiteralPath (Join-Path $stage 'QRCODE-GENERATOR-LICENSE.txt') -Encoding UTF8

$manifest = [ordered]@{
    channel = 'integration'
    gitCommit = $commit
    createdAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    apiMajor = 1
    bindingProfile = 'gtbikev-0804'
    gtbikevAssemblyVersion = '0.8.0.4'
    scriptHookVDotNetReference = [Reflection.AssemblyName]::GetAssemblyName($hook).Version.ToString()
    scriptHookVDotNetReferenceSha256 = (Get-FileHash -LiteralPath $hook).Hash.ToLowerInvariant()
    nodeUsedToBuild = (& node --version)
    files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            name = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        }
    })
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding UTF8
$archive = Join-Path $destination "$name.zip"
Compress-Archive -LiteralPath $stage -DestinationPath $archive -ErrorAction Stop
Write-Host "Integration package: $archive"
Write-Host "SHA-256: $((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant())"
