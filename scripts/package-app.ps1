#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Package the Teams app for sideloading.
.DESCRIPTION
    Creates a .zip file from the appPackage folder with the BOT_ID placeholder
    replaced by the actual bot App ID. The resulting zip can be sideloaded
    directly into Microsoft Teams.
.PARAMETER BotAppId
    The Entra ID App Registration (client) ID for the bot.
    If not provided, reads from BOT_APP_ID environment variable or azd env.
.PARAMETER OutputPath
    Path for the output zip file. Defaults to ./appPackage.zip
.EXAMPLE
    ./scripts/package-app.ps1 -BotAppId "00000000-0000-0000-0000-000000000000"
.EXAMPLE
    ./scripts/package-app.ps1   # reads BOT_APP_ID from env or azd
#>

param(
    [string]$BotAppId,
    [string]$OutputPath = "appPackage.zip"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Packaging Teams App ===" -ForegroundColor Cyan

# Resolve bot App ID
if (-not $BotAppId) {
    $BotAppId = $env:BOT_APP_ID
}
if (-not $BotAppId) {
    try { $BotAppId = azd env get-value BOT_APP_ID 2>$null } catch {}
}
if (-not $BotAppId) {
    Write-Error "Bot App ID not found. Provide -BotAppId parameter, set BOT_APP_ID env var, or run 'azd env set BOT_APP_ID <value>'."
    exit 1
}

Write-Host "Bot App ID: $BotAppId"

# Create temp staging directory
$stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "contoso-app-package-$(Get-Random)"
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

try {
    # Copy manifest and replace placeholders
    $manifestContent = Get-Content "appPackage/manifest.json" -Raw
    $manifestContent = $manifestContent -replace '\$\{\{BOT_ID\}\}', $BotAppId
    $manifestContent | Set-Content (Join-Path $stagingDir "manifest.json") -Encoding UTF8

    # Validate the manifest has no remaining placeholders
    if ($manifestContent -match '\$\{\{') {
        Write-Warning "Manifest still contains unresolved placeholders: $($Matches[0])"
    }

    # Copy icon files
    Copy-Item "appPackage/color.png" $stagingDir
    Copy-Item "appPackage/outline.png" $stagingDir

    # Create zip
    $outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
    if (Test-Path $outputFullPath) {
        Remove-Item $outputFullPath -Force
    }

    Compress-Archive -Path "$stagingDir/*" -DestinationPath $outputFullPath -Force

    $zipSize = (Get-Item $outputFullPath).Length
    Write-Host ""
    Write-Host "Package created: $outputFullPath ($zipSize bytes)" -ForegroundColor Green
    Write-Host ""
    Write-Host "To install in Teams:" -ForegroundColor Yellow
    Write-Host "  1. Open Microsoft Teams"
    Write-Host "  2. Go to Apps > Manage your apps > Upload a custom app"
    Write-Host "  3. Select: $outputFullPath"
    Write-Host ""
}
finally {
    # Clean up staging directory
    Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
}
