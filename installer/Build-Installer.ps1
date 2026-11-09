# Builds outputs\release\GTAVRidingCompanion-Setup.exe in one step:
#   dependencies (npm ci when needed) -> tests -> bridge bundle -> payload (Node.js, adapter sources, map) -> setup exe.
# Easiest: double-click build-installer.cmd in the repository root, or run `npm run build:installer`.
#   -SkipTests      do not run npm test (faster rebuilds while iterating on the window)
#   -Clean          reinstall node_modules with npm ci even if they look current
#   -OutputDirectory PATH   default outputs\release
param([string]$OutputDirectory, [switch]$SkipTests, [switch]$Clean)
$ErrorActionPreference = 'Stop'
$started = Get-Date
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'outputs\release' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$step = 0
function Step([string]$text) { $script:step++; Write-Host ""; Write-Host "[$script:step/7] $text" -ForegroundColor Cyan }
# Native tools write warnings to stderr; Windows PowerShell 5.1 would turn those into terminating errors under Stop.
# Exit codes decide success instead.
function Native([scriptblock]$block) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $block 2>&1 | ForEach-Object { "  $_" } } finally { $ErrorActionPreference = $old }
}
function Fail([string]$text) { Write-Host ""; Write-Host "BUILD FAILED: $text" -ForegroundColor Red; exit 1 }

Step 'Checking the tools'
$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeCommand) { Fail 'Node.js is not installed. Install Node.js 22.2 or later from https://nodejs.org and open a new terminal.' }
$node = $nodeCommand.Source
$nodeVersion = (& $node --version).TrimStart('v')
if ([version]$nodeVersion -lt [version]'22.2.0') { Fail "Node.js $nodeVersion is too old; 22.2 or later is required." }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { Fail 'The 64-bit .NET Framework 4.8 compiler (csc.exe) was not found. Install .NET Framework 4.8.' }
$package = Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json
$version = $package.version
Write-Host "  Node.js $nodeVersion ($node)"
Write-Host "  App version $version (package.json)"

Step 'Installing dependencies'
Push-Location $root
try {
    $marker = Join-Path $root 'node_modules\.package-lock.json'
    $lock = Join-Path $root 'package-lock.json'
    $current = (Test-Path -LiteralPath $marker) -and (Test-Path -LiteralPath (Join-Path $root 'node_modules\.bin\esbuild.cmd')) -and
        ((Get-Item -LiteralPath $marker).LastWriteTimeUtc -ge (Get-Item -LiteralPath $lock).LastWriteTimeUtc)
    if ($Clean -or -not $current) {
        Native { & npm ci --no-audit --no-fund }
        if ($LASTEXITCODE -ne 0) { Fail 'npm ci failed (see above).' }
    } else { Write-Host '  node_modules is up to date (use -Clean to reinstall).' }

    Step 'Running the tests'
    if ($SkipTests) { Write-Host '  Skipped (-SkipTests).' -ForegroundColor Yellow }
    else {
        Native { & npm test --silent } | Where-Object { $_ -match 'not ok|✖|# (tests|pass|fail)|(tests|pass|fail) \d' }
        if ($LASTEXITCODE -ne 0) { Fail 'Tests failed. Fix them or rebuild with -SkipTests.' }
    }
} finally { Pop-Location }

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$work = Join-Path $OutputDirectory ('build-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'payload'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    Step 'Bundling the bridge'
    $bundle = Join-Path $stage 'bridge-v1.mjs'
    Native { & (Join-Path $root 'node_modules\.bin\esbuild.cmd') (Join-Path $root 'bridge\start-v1.mjs') --bundle --platform=node --format=esm --target=node22 --log-level=warning "--outfile=$bundle" }
    if ($LASTEXITCODE -ne 0) { Fail 'Bundling the bridge failed.' }
    Native { & $node --check $bundle }
    if ($LASTEXITCODE -ne 0) { Fail 'The bridge bundle has a syntax error.' }

    Step 'Collecting the app files'
    Copy-Item -LiteralPath $node -Destination (Join-Path $stage 'node.exe')
    Copy-Item -LiteralPath (Join-Path $root 'bridge\Enable-LanAccess.ps1') -Destination $stage
    New-Item -ItemType Directory -Path (Join-Path $stage 'adapter-src') | Out-Null
    Copy-Item -Path (Join-Path $root 'bridge\game-adapter-v1\*.cs') -Destination (Join-Path $stage 'adapter-src')
    $resources = Join-Path $PSScriptRoot 'resources'
    New-Item -ItemType Directory -Path (Join-Path $stage 'resources') | Out-Null
    # The map image itself is not shipped: the Mod tab downloads it on request (see imageUrl in the calibration file).
    foreach ($name in @('gta-map.calibration.json')) {
        $file = Join-Path $resources $name
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $stage 'resources') }
        else { Write-Host "  installer\resources\$name is missing: the Mod tab will have no map." -ForegroundColor Yellow }
    }
    Set-Content -LiteralPath (Join-Path $stage 'version.txt') -Value $version -Encoding ASCII -NoNewline
    Copy-Item -LiteralPath (Join-Path $root 'node_modules\@garmin\fitsdk\LICENSE.txt') -Destination (Join-Path $stage 'GARMIN-FIT-SDK-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses\QRCODE-GENERATOR-LICENSE.txt') -Destination $stage
    # The Node.js license is downloaded once per Node version and cached, so later builds work offline.
    $cache = Join-Path $root 'outputs\cache'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $nodeLicense = Join-Path $cache "NODE-LICENSE-v$nodeVersion.txt"
    if (-not (Test-Path -LiteralPath $nodeLicense)) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/nodejs/node/v$nodeVersion/LICENSE" -OutFile $nodeLicense
            if ((Get-Item -LiteralPath $nodeLicense).Length -lt 1000) { Remove-Item -LiteralPath $nodeLicense; throw 'incomplete download' }
        } catch {
            $older = Get-ChildItem -LiteralPath $cache -Filter 'NODE-LICENSE-v*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $older) { Fail "Could not download the Node.js license ($($_.Exception.Message)). Connect to the internet once and build again." }
            Write-Host "  Offline: using the cached license of $($older.Name)." -ForegroundColor Yellow
            $nodeLicense = $older.FullName
        }
    }
    Copy-Item -LiteralPath $nodeLicense -Destination (Join-Path $stage 'NODE-LICENSE.txt')
    $hashes = Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
        '{0} {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
    }
    $hashes | Set-Content -LiteralPath (Join-Path $stage 'payload-sha256.txt') -Encoding ASCII
    $zip = Join-Path $work 'BridgePayload.zip'
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

    Step 'Compiling the Windows app'
    $buildInfo = Join-Path $work 'BuildInfo.cs'
    $numeric = ($version -replace '[^0-9.].*$', '')
    if (($numeric.Split('.')).Count -lt 3) { $numeric = '0.0.0' }
    @"
using System.Reflection;
[assembly: AssemblyTitle("GTAV Riding Companion")]
[assembly: AssemblyProduct("GTAV Riding Companion")]
[assembly: AssemblyDescription("Phone bridge, GTBike V settings and live map")]
[assembly: AssemblyVersion("$numeric.0")]
[assembly: AssemblyFileVersion("$numeric.0")]
[assembly: AssemblyInformationalVersion("$version")]
namespace GTAVRidingCompanion { static class BuildInfo { internal const string Version = "$version"; } }
"@ | Set-Content -LiteralPath $buildInfo -Encoding UTF8
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }) + $buildInfo
    $output = Join-Path $OutputDirectory 'GTAVRidingCompanion-Setup.exe'
    $icon = Join-Path $resources 'app.ico'
    $cscArguments = @('/nologo', '/warnaserror+', '/target:winexe', '/platform:x64', '/optimize+', "/out:$output", "/resource:$zip,BridgePayload.zip",
        '/reference:System.Core.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll',
        '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll')
    if (Test-Path -LiteralPath $icon) { $cscArguments += "/win32icon:$icon"; $cscArguments += "/resource:$icon,AppIcon.ico" }
    $cscArguments += $sources
    Native { & $compiler @cscArguments }
    if ($LASTEXITCODE -ne 0) { Fail 'The Windows app did not compile (see the errors above).' }

    Step 'Done'
    $hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  GTAVRidingCompanion-Setup.exe" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'GTAVRidingCompanion-Setup.sha256.txt') -Encoding ASCII
    $size = [math]::Round((Get-Item -LiteralPath $output).Length / 1MB, 1)
    Write-Host "  Installer: $OutputDirectory" -ForegroundColor Green
    Write-Host "  SHA-256:   $hash"
    Write-Host ("  Took {0:0}s. Copy the .exe to the gaming PC and run it; Node.js is not needed there." -f ((Get-Date) - $started).TotalSeconds)
} finally {
    $releaseRoot = $OutputDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($work).StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $work)) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
