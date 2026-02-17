targetScope = 'subscription'

@description('Primary location for all resources')
param location string

@description('Environment name (e.g., dev, prod)')
param environmentName string

@description('Bot App Registration ID')
@secure()
param botAppId string

@description('Bot App Registration Secret')
@secure()
param botAppSecret string

@description('Azure AD Tenant ID')
param tenantId string

@description('Model deployment name for agents')
param modelDeployment string = 'gpt-4o-mini'

var abbrs = loadJsonContent('abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = {
  'azd-env-name': environmentName
  'project': 'contoso-agent-hub'
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: '${abbrs.resourceGroup}${environmentName}'
  location: location
  tags: tags
}

module ai 'modules/ai.bicep' = {
  name: 'ai'
  scope: rg
  params: {
    location: location
    name: '${abbrs.cognitiveServicesAccount}${resourceToken}'
    projectName: 'project-${resourceToken}'
    modelDeployment: modelDeployment
    tags: tags
  }
}

module acr 'modules/acr.bicep' = {
  name: 'acr'
  scope: rg
  params: {
    location: location
    name: '${abbrs.containerRegistry}${resourceToken}'
    tags: tags
  }
}

module containerApp 'modules/container-app.bicep' = {
  name: 'container-app'
  scope: rg
  params: {
    location: location
    name: '${abbrs.containerApp}bot-${resourceToken}'
    environmentName: '${abbrs.containerAppsEnvironment}${resourceToken}'
    botAppId: botAppId
    botAppSecret: botAppSecret
    tenantId: tenantId
    foundryProjectEndpoint: ai.outputs.projectEndpoint
    opsAgentName: 'ContosoOpsAgent'
    menuAgentName: 'ContosoMenuAgent'
    orchestratorAgentName: 'ContosoOrchestratorAgent'
    modelDeployment: modelDeployment
    acrLoginServer: acr.outputs.loginServer
    acrAdminUsername: acr.outputs.adminUsername
    acrAdminPassword: acr.outputs.adminPassword
    tags: tags
  }
}

// RBAC: Grant Container App MI roles on AI Services + project MI AcrPull on ACR
module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  scope: rg
  params: {
    aiAccountId: ai.outputs.aiAccountId
    containerAppPrincipalId: containerApp.outputs.principalId
    projectPrincipalId: ai.outputs.projectPrincipalId
    acrId: acr.outputs.id
  }
}

module bot 'modules/bot-service.bicep' = {
  name: 'bot-service'
  scope: rg
  params: {
    location: 'global'
    name: '${abbrs.botService}${resourceToken}'
    botAppId: botAppId
    tenantId: tenantId
    botEndpoint: 'https://${containerApp.outputs.fqdn}/api/messages'
    tags: tags
  }
}

output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = acr.outputs.loginServer
output FOUNDRY_PROJECT_ENDPOINT string = ai.outputs.projectEndpoint
output AI_ENDPOINT string = ai.outputs.aiEndpoint
output FOUNDRY_ACR string = acr.outputs.loginServer
output BOT_ENDPOINT string = 'https://${containerApp.outputs.fqdn}/api/messages'
output CONTAINER_APP_NAME string = containerApp.outputs.name
