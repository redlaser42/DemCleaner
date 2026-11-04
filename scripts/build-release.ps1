# Build and package DEM Cleaner for release distribution
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$releaseFolderName = "DemCleaner-v$Version-win-x64"
$releaseDir = Join-Path $repoRoot "release\$releaseFolderName"
$zipPath = Join-Path $repoRoot "release\$releaseFolderName.zip"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Building DEM Cleaner v$Version ($Configuration)" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. Clean previous release artifacts
if (Test-Path $releaseDir) {
    Write-Host "Cleaning existing release directory: $releaseDir" -ForegroundColor Yellow
    Remove-Item $releaseDir -Recurse -Force
}
if (Test-Path $zipPath) {
    Write-Host "Cleaning existing zip archive: $zipPath" -ForegroundColor Yellow
    Remove-Item $zipPath -Force
}

# Ensure release parent folder exists
$parentRelease = Join-Path $repoRoot "release"
if (-not (Test-Path $parentRelease)) {
    New-Item -ItemType Directory -Path $parentRelease | Out-Null
}

# 2. Publish
Write-Host "`nPublishing project..." -ForegroundColor Green
dotnet publish DemCleaner.csproj -c $Configuration -o $releaseDir --no-restore

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
}

# 3. Copy License and Quickstart
Copy-Item (Join-Path $repoRoot "LICENSE.txt") (Join-Path $releaseDir "LICENSE.txt") -Force

$quickstartText = @"
================================================================================
 DEM Cleaner - v$Version
 By Sipp Studio
================================================================================

Turn raw elevation tiles into clean, continuous terrain heightmaps.

SYSTEM REQUIREMENTS
-------------------
- Windows 10 or Windows 11 (64-bit)
- Microsoft .NET 8.0 Desktop Runtime (x64)
  If not installed, download from: https://dotnet.microsoft.com/download/dotnet/8.0

QUICK START
-----------
1. Double-click DemCleaner.exe to start.
2. Drag and drop DEM GeoTIFF files or use 'Browse Files' / 'Browse Folder'.
3. Configure NoData detection and void filling method.
4. Smooth tile seams and preview your terrain in 2D or 3D.
5. Choose your target game engine preset (Unreal Engine, Unity, Power-of-Two)
   and export 16-bit PNG heightmaps or GeoTIFFs.

DOCUMENTATION & REPOSITORY
--------------------------
GitHub: https://github.com/sippstudio/dem-cleaner

LICENSE
-------
DEM Cleaner is proprietary software. Copyright (C) Sipp Studio.
All rights reserved. See LICENSE.txt for full terms.
"@

Set-Content -Path (Join-Path $releaseDir "README.txt") -Value $quickstartText -Encoding utf8

# 4. Smoke test published binary
Write-Host "`nRunning smoke test on published executable..." -ForegroundColor Green
$proc = Start-Process -FilePath (Join-Path $releaseDir "DemCleaner.exe") -ArgumentList "--self-test" -WorkingDirectory $releaseDir -Wait -PassThru
$testResultFile = Join-Path $releaseDir "selftest_result.txt"

if (Test-Path $testResultFile) {
    $resultText = Get-Content $testResultFile -Raw
    Write-Host "Self-Test Result: $resultText" -ForegroundColor Green
    Remove-Item $testResultFile -Force
} else {
    Write-Host "Warning: selftest_result.txt not generated." -ForegroundColor Yellow
}

# 5. Create Zip Archive
Write-Host "`nPackaging release zip archive..." -ForegroundColor Green
Compress-Archive -Path "$releaseDir\*" -DestinationPath $zipPath -Force

$zipItem = Get-Item $zipPath
$zipSizeMb = [math]::Round($zipItem.Length / 1MB, 2)

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host " Release Package Created Successfully!" -ForegroundColor Green
Write-Host " Folder:  $releaseDir"
Write-Host " Archive: $zipPath ($zipSizeMb MB)"
Write-Host "==========================================" -ForegroundColor Cyan
