param([string]$ReferenceRoot = (Join-Path $PSScriptRoot '..\..\reference\GTBikeV-0.8.0.4-decompiled\GTBikeV'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot '..\outputs\gears-test'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$source = Join-Path $ReferenceRoot 'VirtualGearBox.cs'
if (-not (Test-Path -LiteralPath $source)) { throw 'Pass -ReferenceRoot pointing to the static GTBikeV 0.8.0.4 reference.' }
# Restore IL byte conversions and disambiguate the short overload in this generated C# reference copy.
$native = (Get-Content -LiteralPath $source -Raw).Replace('this.GearIndex = gearIndex2 + 1;', 'this.GearIndex = (byte)(gearIndex2 + 1);').Replace('this.GearIndex = gearIndex2 - 1;', 'this.GearIndex = (byte)(gearIndex2 - 1);')
$nativePath = Join-Path $output 'VirtualGearBox0804.cs'
$native = $native.Replace('Math.Max(0, num6)', 'Math.Max((short)0, num6)')
Set-Content -LiteralPath $nativePath -Value $native -Encoding UTF8
$exe = Join-Path $output 'GearsHarness.exe'
& $compiler /nologo /warnaserror+ /nowarn:0219,0414 /target:exe /platform:x64 "/out:$exe" `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Gears.cs') `
    (Join-Path $PSScriptRoot '..\tests\GearsHarness.cs') $nativePath
if ($LASTEXITCODE -ne 0) { throw 'Native gears harness compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Native gears harness failed.' }
