#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Post-provision hook: Create account-level capability host, verify data-plane.
.DESCRIPTION
    Called automatically by `azd provision`. Creates the account-level capability host
    via REST API (2025-10-01-preview) with enablePublicHostingEnvironment=true, then
    waits for the data-plane to become ready.

    The capability host is required for the Microsoft Foundry portal "Start agent deployment"
    button and for hosted agent container lifecycle management. Without it the portal
    calls to agentContainerActionsResolver return 404.

    Only creates an ACCOUNT-level capability host. Do NOT create a project-level one.
    That triggers ML Hub auto-creation which MCAPS governance policies lock down
    (AIFoundryHub_PublicNetwork_Modify -> publicNetworkAccess: Disabled).
#>

param()

Write-Host "=== Post-Provision: Capability Host + Verification ===" -ForegroundColor Cyan

$rgName = azd env get-value AZURE_RESOURCE_GROUP
$projectEndpoint = azd env get-value FOUNDRY_PROJECT_ENDPOINT

if (-not $rgName -or -not $projectEndpoint) {
    Write-Error "Missing required azd environment values. Run 'azd provision' first."
    exit 1
}

Write-Host "Resource Group: $rgName"
Write-Host "Project Endpoint: $projectEndpoint"

# Extract AI account name from the project endpoint
# Format: https://<account>.services.ai.azure.com/api/projects/<project>
$accountName = ($projectEndpoint -replace 'https://', '' -split '\.')[0]
Write-Host "AI Account: $accountName"

$subscriptionId = az account show --query id -o tsv

# --- Create account-level capability host ---
# Uses 2025-10-01-preview because 2025-04-01-preview Bicep schema lacks enablePublicHostingEnvironment.
$capHostUrl = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$rgName/providers/Microsoft.CognitiveServices/accounts/$accountName/capabilityHosts/default?api-version=2025-10-01-preview"
$bodyFile = Join-Path $env:TEMP "caphost-body.json"
Set-Content -Path $bodyFile -Value '{"properties":{"capabilityHostKind":"Agents","enablePublicHostingEnvironment":true}}' -Encoding UTF8

Write-Host "Creating account-level capability host..." -ForegroundColor Yellow
$result = az rest --url $capHostUrl --method PUT --body "@$bodyFile" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Capability host creation failed: $result"
    exit 1
}

# Poll for completion (up to 5 minutes)
$maxWait = 300
$elapsed = 0
$interval = 15
while ($elapsed -lt $maxWait) {
    $state = az rest --url $capHostUrl --method GET --query "properties.provisioningState" -o tsv 2>&1
    Write-Host "  Capability host: $state (${elapsed}s elapsed)"
    if ($state -eq "Succeeded") { Write-Host "Capability host ready." -ForegroundColor Green; break }
    if ($state -eq "Failed") {
        Write-Error "Capability host provisioning failed."
        exit 1
    }
    Start-Sleep -Seconds $interval
    $elapsed += $interval
}
if ($elapsed -ge $maxWait) {
    Write-Error "Capability host timed out after ${maxWait}s."
    exit 1
}

# --- Verify project on management plane ---
$projectName = ($projectEndpoint -split '/projects/')[1]
$mgmtUrl = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$rgName/providers/Microsoft.CognitiveServices/accounts/$accountName/projects/${projectName}?api-version=2025-04-01-preview"
$projState = az rest --url $mgmtUrl --method GET --query "properties.provisioningState" -o tsv 2>&1
if ($projState -eq "Succeeded") {
    Write-Host "Project '$projectName' provisioning state: Succeeded" -ForegroundColor Green
} else {
    Write-Host "Warning: Project state is '$projState'." -ForegroundColor Yellow
}

# --- Wait for data-plane readiness ---
$token = az account get-access-token --resource "https://ai.azure.com" --query accessToken -o tsv
$headers = @{ "Authorization" = "Bearer $token"; "Content-Type" = "application/json" }
$dataPlaneUrl = "$projectEndpoint/agents?api-version=2025-11-15-preview"

$maxWait = 180
$elapsed = 0
$interval = 15
Write-Host "Waiting for data-plane readiness..." -ForegroundColor Yellow
while ($elapsed -lt $maxWait) {
    try {
        $resp = Invoke-RestMethod -Uri $dataPlaneUrl -Headers $headers -Method GET -ErrorAction Stop
        Write-Host "Data-plane is ready." -ForegroundColor Green
        break
    } catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        Write-Host "  Data-plane: HTTP $statusCode (${elapsed}s elapsed)"
    }
    Start-Sleep -Seconds $interval
    $elapsed += $interval
}

if ($elapsed -ge $maxWait) {
    Write-Host "Warning: Data-plane not ready after ${maxWait}s. Rerun 'azd deploy' after a few minutes." -ForegroundColor Yellow
}

# --- Grant RBAC to the auto-generated Agent Identity ---
# Hosted agent containers run under this identity. Without OpenAI User + AI Developer
# roles, containers start but fail to call Azure OpenAI (RequestTimedOut at the proxy).
Write-Host "Checking for agent identity on project..." -ForegroundColor Yellow
$agentIdentityId = az rest --url $mgmtUrl --method GET --query "properties.agentIdentity.agentIdentityId" -o tsv 2>&1

if ($agentIdentityId -and $agentIdentityId -ne "None" -and $agentIdentityId.Length -gt 10) {
    Write-Host "Agent Identity principal: $agentIdentityId" -ForegroundColor Green

    $aiAccountResourceId = "/subscriptions/$subscriptionId/resourceGroups/$rgName/providers/Microsoft.CognitiveServices/accounts/$accountName"
    $openAIUserRoleId = "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd"
    $aiDeveloperRoleId = "64702f94-c441-49e6-a78b-ef80e0188fee"

    foreach ($pair in @(@("Cognitive Services OpenAI User", $openAIUserRoleId), @("Azure AI Developer", $aiDeveloperRoleId))) {
        $roleName = $pair[0]
        $roleId = $pair[1]
        Write-Host "  Assigning '$roleName' to agent identity..."
        az role assignment create `
            --assignee-object-id $agentIdentityId `
            --assignee-principal-type ServicePrincipal `
            --role $roleId `
            --scope $aiAccountResourceId `
            --only-show-errors 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "    Assigned." -ForegroundColor Green
        } else {
            # May already exist from a previous run
            Write-Host "    Already assigned or failed (non-blocking)." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "Warning: Agent identity not yet available on project. Assign RBAC manually after first agent deploy." -ForegroundColor Yellow
    Write-Host "  Run: az role assignment create --assignee <agentIdentityId> --role 'Cognitive Services OpenAI User' --scope <aiAccountId>" -ForegroundColor Yellow
}

Write-Host "=== Post-Provision Complete ===" -ForegroundColor Cyan
