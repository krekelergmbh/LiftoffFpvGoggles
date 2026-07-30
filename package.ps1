# Builds the plugin and packs a release ZIP into dist\.
#
# The ZIP mirrors the folder layout a user drops into their BepInEx installation, so the
# whole install instruction is "extract into the BepInEx folder".

param([string]$GameDir, [string]$BepInExDir)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $root "build.ps1") -GameDir $GameDir -BepInExDir $BepInExDir -NoInstall
if ($LASTEXITCODE -gt 0) { throw "Build failed" }

$dll = Join-Path $root "build\LiftoffFpvGoggles.dll"
if (-not (Test-Path $dll)) { throw "Build output missing: $dll" }

# Read the version from the BepInPlugin attribute, the one place it is actually declared, so
# the ZIP name cannot drift from what the game reports in its log.
$pluginSource = Get-Content (Join-Path $root "src\FpvGogglesPlugin.cs") -Raw
$match = [regex]::Match($pluginSource, 'BepInPlugin\(\s*Guid\s*,\s*"[^"]*"\s*,\s*"([^"]+)"')
if (-not $match.Success) { throw "Could not read the version from src\FpvGogglesPlugin.cs" }
$version = $match.Groups[1].Value

$staging = Join-Path $root "dist\staging"
$pluginDir = Join-Path $staging "plugins\LiftoffFpvGoggles"

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null

Copy-Item $dll $pluginDir

# The shader bundle goes next to the DLL, which is where the plugin looks for it. Missing it is
# a broken release rather than a smaller one, so stop instead of shipping a mod whose headline
# feature silently does nothing.
$bundle = Join-Path $root "assets\fpvanalog"
if (-not (Test-Path $bundle)) {
    throw "No shader bundle at $bundle. Run build-bundle.ps1 first, or the release would ship without composite video."
}
Copy-Item $bundle $pluginDir

# install.ps1 sits at the top of the ZIP so it is the first thing anyone sees after
# extracting; README and LICENSE ride along next to the DLL.
Copy-Item (Join-Path $root "install.ps1") $staging
Copy-Item (Join-Path $root "README.md") $pluginDir
Copy-Item (Join-Path $root "LICENSE") $pluginDir

$zip = Join-Path $root "dist\LiftoffFpvGoggles-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force -Confirm:$false }

Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip
Remove-Item $staging -Recurse -Force -Confirm:$false

Write-Host "OK -> $zip" -ForegroundColor Green
