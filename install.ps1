# cxnvmon Installer for Windows
# Usage: irm https://raw.githubusercontent.com/nickprotop/cxnvmon/main/install.ps1 | iex
# Copyright (c) Nikolaos Protopapas. All rights reserved.
# Licensed under the MIT License.

$ErrorActionPreference = "Stop"

$repo = "nickprotop/cxnvmon"
$installDir = "$env:LOCALAPPDATA\cxnvmon"

Write-Host "Installing cxnvmon..." -ForegroundColor Cyan

# Detect architecture
$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
switch ($arch) {
    "X64"   { $binary = "cxnvmon-win-x64.exe" }
    "Arm64" { $binary = "cxnvmon-win-arm64.exe" }
    default {
        Write-Host "Error: Unsupported architecture: $arch" -ForegroundColor Red
        exit 1
    }
}

# Get latest release
Write-Host "Fetching latest release..."
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest"
$version = $release.tag_name -replace '^v', ''
$asset = $release.assets | Where-Object { $_.name -eq $binary }

if (-not $asset) {
    Write-Host "Error: Binary '$binary' not found in release $($release.tag_name)" -ForegroundColor Red
    exit 1
}

Write-Host "Latest version: $version"

# Create directories
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

# Download binary
Write-Host "Downloading $binary..."
$outputPath = Join-Path $installDir "cxnvmon.exe"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $outputPath

# Download uninstaller
$uninstallUrl = "https://raw.githubusercontent.com/$repo/main/uninstall.ps1"
$uninstallPath = Join-Path $installDir "cxnvmon-uninstall.ps1"
Invoke-WebRequest -Uri $uninstallUrl -OutFile $uninstallPath

# Add to PATH if not already there
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$installDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$installDir;$userPath", "User")
    Write-Host "Added $installDir to user PATH" -ForegroundColor Green
}

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  cxnvmon v$version installed!" -ForegroundColor Green
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Binary:  $outputPath"
Write-Host ""
Write-Host "  Run:     cxnvmon"
Write-Host "  Remove:  cxnvmon-uninstall.ps1"
Write-Host ""
Write-Host "  Note: Restart your terminal for PATH changes to take effect." -ForegroundColor Yellow
