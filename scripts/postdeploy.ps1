#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Post-deploy hook: Build agent containers and deploy hosted agents.
.DESCRIPTION
    Called automatically by `azd deploy`. Builds the ops-agent, menu-agent,
    and orchestrator-agent Docker images, pushes them to ACR, and creates
    hosted agent versions via the Azure AI Foundry SDK.
#>

param()

# Do NOT use $ErrorActionPreference = "Stop" — PS 5.1 treats any stderr output
# from native commands (az, docker, pip) as a terminating error, even with 2>&1.
# Instead, check $LASTEXITCODE after each native command.

Write-Host "=== Post-Deploy: Building and Deploying Hosted Agents ===" -ForegroundColor Cyan

# Get azd environment values
$acrLoginServer = azd env get-value FOUNDRY_ACR
$projectEndpoint = azd env get-value FOUNDRY_PROJECT_ENDPOINT
$aiEndpoint = azd env get-value AI_ENDPOINT

if (-not $acrLoginServer -or -not $projectEndpoint) {
    Write-Error "Missing required azd environment values."
    exit 1
}

# Login to ACR
Write-Host "Logging into ACR: $acrLoginServer" -ForegroundColor Yellow
$acrName = $acrLoginServer -replace '\.azurecr\.io', ''
az acr login --name $acrName 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "ACR login failed."
    exit 1
}

# Build and push agent images using ACR build tasks (cloud-side, always amd64)
$agents = @(
    @{ Name = "ops-agent"; Image = "contoso-ops-agent:v1"; Path = "agents/ops-agent" },
    @{ Name = "menu-agent"; Image = "contoso-menu-agent:v1"; Path = "agents/menu-agent" },
    @{ Name = "orchestrator-agent"; Image = "contoso-orchestrator-agent:v1"; Path = "agents/orchestrator" }
)

foreach ($agent in $agents) {
    Write-Host "Building $($agent.Name) via ACR build tasks..." -ForegroundColor Yellow
    # --no-logs prevents UnicodeEncodeError on Windows cp1252 terminals from streaming build logs
    az acr build --registry $acrName --image $agent.Image --platform linux/amd64 --no-logs $agent.Path 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "ACR build failed for $($agent.Name)."
        exit 1
    }
    Write-Host "$($agent.Name) built and pushed." -ForegroundColor Green
}

# Deploy agents via Python SDK
Write-Host "Installing deploy script dependencies..." -ForegroundColor Yellow
pip install -r agents/requirements.txt --quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "pip install failed."
    exit 1
}

Write-Host "Deploying hosted agents via SDK..." -ForegroundColor Yellow
$env:FOUNDRY_PROJECT_ENDPOINT = $projectEndpoint
$env:FOUNDRY_ACR = $acrLoginServer
$env:AI_ENDPOINT = $aiEndpoint
$modelDeployment = azd env get-value MODEL_DEPLOYMENT
if (-not $modelDeployment) { $modelDeployment = "gpt-4o-mini" }
$env:MODEL_DEPLOYMENT = $modelDeployment

python agents/deploy_hosted_agents.py

Write-Host "=== Post-Deploy Complete ===" -ForegroundColor Cyan
