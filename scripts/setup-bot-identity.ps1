#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Create an Entra ID App Registration for the Teams bot.
.DESCRIPTION
    Creates a single-tenant app registration and generates a client secret.
    Outputs the values needed for .env / azd environment.
#>

param(
    [string]$DisplayName = "Contoso Agent Hub",
    [string]$TenantId
)

$ErrorActionPreference = "Stop"

Write-Host "=== Creating Entra ID App Registration ===" -ForegroundColor Cyan

if (-not $TenantId) {
    $TenantId = (az account show --query tenantId -o tsv)
}

# Create app registration
$app = az ad app create `
    --display-name $DisplayName `
    --sign-in-audience AzureADMyOrg `
    --query "{appId: appId, id: id}" -o json | ConvertFrom-Json

Write-Host "App ID: $($app.appId)" -ForegroundColor Green

# Create client secret (valid 2 years)
$secret = az ad app credential reset `
    --id $app.id `
    --display-name "Bot Secret" `
    --years 2 `
    --query password -o tsv

Write-Host ""
Write-Host "=== Add these to your .env file ===" -ForegroundColor Yellow
Write-Host "BOT_APP_ID=$($app.appId)"
Write-Host "BOT_APP_SECRET=$secret"
Write-Host "TENANT_ID=$TenantId"

# Optionally set in azd environment
$setAzd = Read-Host "Set these in azd environment? (y/n)"
if ($setAzd -eq 'y') {
    azd env set BOT_APP_ID $app.appId
    azd env set BOT_APP_SECRET $secret
    azd env set TENANT_ID $TenantId
    Write-Host "Values set in azd environment." -ForegroundColor Green
}

Write-Host "=== Done ===" -ForegroundColor Cyan
