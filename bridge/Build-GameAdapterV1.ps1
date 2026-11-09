# Builds the v1 adapter (GTBikeVCompanionV1.dll). Independent of the legacy GTBikeVCompanion.dll, which stays as is.
# -Install copies it into GTA V\scripts with a dated backup of any previous copy. Run with GTA V closed.
param([Parameter(Mandatory = $true)][string]$GtaDirectory, [switch]$Install)
$ErrorActionPreference = 'Stop'
$gtaRoot = (Resolve-Path -LiteralPath $GtaDirectory).Path
$reference = Join-Path $gtaRoot 'ScriptHookVDotNet3.dll'
if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "$reference not found. Choose the GTA V folder where ScriptHookVDotNet3 is installed." }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '64-bit .NET Framework 4.8 is required to build the adapter.' }
$outputDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\outputs\game-adapter-v1'))
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$output = Join-Path $outputDirectory 'GTBikeVCompanionV1.dll'
& $compiler /nologo /warnaserror+ /target:library /platform:x64 /optimize+ "/out:$output" "/reference:$reference" /reference:System.Web.Extensions.dll `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Binding.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Gears.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1KeyboardTrainer.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1KeyboardHost.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Camera.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1CameraNative.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Extensions.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1More.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1World.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Teleport.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Destination.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Addons.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Alleycat.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1SurvivalSprint.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Ghost.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Discover.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GtbikeV1Progress.cs') `
    (Join-Path $PSScriptRoot 'game-adapter-v1\GTBikeVCompanionV1.cs')
if ($LASTEXITCODE -ne 0) { throw 'The v1 adapter failed to build.' }
Write-Host "v1 adapter built: $output"
if ($Install) {
    $scriptsDirectory = Join-Path $gtaRoot 'scripts'
    if (-not (Test-Path -LiteralPath $scriptsDirectory -PathType Container)) { throw 'The GTA V scripts folder does not exist. Check your ScriptHookVDotNet installation.' }
    $destination = Join-Path $scriptsDirectory 'GTBikeVCompanionV1.dll'
    if (Test-Path -LiteralPath $destination) { Copy-Item -LiteralPath $destination -Destination ($destination + '.' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak') }
    Copy-Item -LiteralPath $output -Destination $destination -Force
    Write-Host "Installed: $destination. Start GTA V in Story Mode."
    Write-Host "Snapshot: $env:LOCALAPPDATA\GTBikeVCompanion\v1\telemetry.json"
}
