# Installs the plugin into the BepInEx folder Rai Pal uses for Liftoff.
#
# Run this from the extracted release folder:
#   Right click -> "Run with PowerShell"
# or from a PowerShell window:
#   .\install.ps1
#
# If Windows blocks the script, either unblock the file in its Properties dialog or run:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1

param(
    [string]$BepInExDir,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-BepInExDir {
    $modsRoot = Join-Path $env:APPDATA "raicuparta\rai-pal\data\installed-mods"
    if (-not (Test-Path $modsRoot)) { return $null }

    # Rai Pal keys folders by game id, so pick the one that actually has UUVR in it.
    foreach ($dir in (Get-ChildItem $modsRoot -Directory)) {
        $candidate = Join-Path $dir.FullName "bepinex\BepInEx"
        if (Test-Path (Join-Path $candidate "plugins\uuvr-mono-modern")) { return $candidate }
    }
    return $null
}

if (-not $BepInExDir) { $BepInExDir = Find-BepInExDir }

if (-not $BepInExDir) {
    Write-Host ""
    Write-Host "Could not find a Rai Pal installation with UUVR in it." -ForegroundColor Red
    Write-Host ""
    Write-Host "This plugin needs UUVR. Install it first:" -ForegroundColor Yellow
    Write-Host "  1. Get Rai Pal:  https://github.com/Raicuparta/rai-pal"
    Write-Host "  2. Find Liftoff: Micro Drones in it"
    Write-Host "  3. Install the mod 'UUVR Mono Modern'"
    Write-Host "  4. Run this script again"
    Write-Host ""
    Write-Host "Already installed elsewhere? Point at it directly:" -ForegroundColor Yellow
    Write-Host "  .\install.ps1 -BepInExDir ""C:\path\to\BepInEx"""
    Write-Host ""
    exit 1
}

$pluginDir = Join-Path $BepInExDir "plugins\LiftoffFpvGoggles"

if ($Uninstall) {
    if (Test-Path $pluginDir) {
        Remove-Item $pluginDir -Recurse -Force -Confirm:$false
        Write-Host "Removed: $pluginDir" -ForegroundColor Green
    } else {
        Write-Host "Nothing to remove; not installed." -ForegroundColor Yellow
    }
    Write-Host "Your settings file was left alone: $BepInExDir\config\maxwo.liftoff.fpvgoggles.cfg" -ForegroundColor DarkGray
    exit 0
}

$dll = Join-Path $root "plugins\LiftoffFpvGoggles\LiftoffFpvGoggles.dll"
if (-not (Test-Path $dll)) { $dll = Join-Path $root "LiftoffFpvGoggles.dll" }
if (-not (Test-Path $dll)) {
    throw "LiftoffFpvGoggles.dll not found next to this script. Extract the whole release ZIP and run the script from inside it."
}

if (-not (Test-Path $pluginDir)) { New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null }

try {
    Copy-Item $dll $pluginDir -Force -ErrorAction Stop
}
catch {
    Write-Host "Could not copy the plugin - is Liftoff still running? Close it and try again." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Installed to: $pluginDir" -ForegroundColor Green
Write-Host ""
Write-Host "How to fly:" -ForegroundColor Cyan
Write-Host "  1. Start Liftoff. It runs flat - VR is off on purpose, so menus work normally."
Write-Host "  2. Pick a track and start the flight."
Write-Host "  3. Press F3. VR switches on with the view locked to the drone."
Write-Host ""
Write-Host "Settings appear after the first start, in:" -ForegroundColor DarkGray
Write-Host "  $BepInExDir\config\maxwo.liftoff.fpvgoggles.cfg" -ForegroundColor DarkGray
Write-Host ""
