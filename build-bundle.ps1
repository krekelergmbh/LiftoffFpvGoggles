<#
.SYNOPSIS
    Builds the composite video shader into the AssetBundle the plugin loads at runtime.

.DESCRIPTION
    Only needed when the shader changes. The built bundle is committed to assets\, so a normal
    build.ps1 does not need Unity installed at all.

    Unity must be the same version Liftoff was built with - 2022.3 - or the bundle may refuse to
    load. Building with an older 2022.3 patch than the game is fine; building with a newer one
    is the direction that breaks.
#>
param(
    [string]$UnityExe,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "unity"
$output = Join-Path $root "assets"

# ---------------------------------------------------------------------------
# Find Unity
# ---------------------------------------------------------------------------

function Find-Unity {
    # The Hub's own folder first, newest 2022.3 last so it wins the sort.
    $roots = @(
        "C:\Program Files\Unity\Hub\Editor",
        "D:\Unity\Hub\Editor",
        "$env:LOCALAPPDATA\Unity\Hub\Editor"
    )

    $found = @()
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        foreach ($dir in Get-ChildItem $r -Directory -ErrorAction SilentlyContinue) {
            $exe = Join-Path $dir.FullName "Editor\Unity.exe"
            if ((Test-Path $exe) -and $dir.Name -like "2022.3*") { $found += $exe }
        }
    }

    # Standalone installers do not use the Hub layout and only leave a registry entry.
    $keys = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    $entries = Get-ItemProperty $keys -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match "^Unity 2022\.3" -and $_.DisplayIcon }
    foreach ($e in $entries) {
        $exe = $e.DisplayIcon -replace '^"|"$', ''
        if (Test-Path $exe) { $found += $exe }
    }

    return ($found | Sort-Object -Unique | Select-Object -Last 1)
}

if (-not $UnityExe) { $UnityExe = Find-Unity }
if (-not $UnityExe -or -not (Test-Path $UnityExe)) {
    throw "No Unity 2022.3 editor found. Install it, or pass -UnityExe 'C:\path\to\Unity.exe'."
}

Write-Host "Unity:   $UnityExe"
Write-Host "Project: $project"

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

if (-not (Test-Path $project)) { throw "Unity project not found at $project" }
New-Item -ItemType Directory -Force -Path $output | Out-Null

$log = Join-Path $env:TEMP "fpvgoggles-bundle-build.log"
if (Test-Path $log) { Remove-Item $log -Force }

Write-Host "Building the asset bundle. The first run also imports the project, which takes a few minutes..."

$unityArgs = @(
    "-batchmode", "-quit", "-nographics",
    "-projectPath", $project,
    "-executeMethod", "LiftoffFpvGogglesBuild.BuildBundle.Build",
    "-logFile", $log
)

$process = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -Wait -PassThru -NoNewWindow
$code = $process.ExitCode

if ($code -ne 0) {
    if (Test-Path $log) {
        Write-Host ""
        Write-Host "--- last 40 lines of $log ---"
        Get-Content $log -Tail 40
    }
    throw "Unity exited with $code. Full log: $log"
}

$bundle = Join-Path $output "fpvanalog"
if (-not (Test-Path $bundle)) { throw "Unity reported success but there is no bundle at $bundle. Log: $log" }

# The manifest files are build metadata; only the bundle itself ships.
Get-ChildItem $output -Filter "*.manifest" | Remove-Item -Force -ErrorAction SilentlyContinue
$stale = Join-Path $output "assets"
if (Test-Path $stale) { Remove-Item $stale -Force -ErrorAction SilentlyContinue }

$size = [math]::Round((Get-Item $bundle).Length / 1KB, 1)
Write-Host ""
Write-Host "OK -> $bundle ($size KB)"
if (-not $Quiet) { Write-Host "Commit it: the bundle is version controlled so building the plugin needs no Unity." }
