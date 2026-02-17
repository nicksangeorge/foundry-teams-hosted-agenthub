@description('Resource ID of the AI Services account')
param aiAccountId string

@description('Principal ID of the Container App managed identity')
param containerAppPrincipalId string

@description('Principal ID of the Foundry project managed identity')
param projectPrincipalId string

@description('Resource ID of the Container Registry')
param acrId string

// Cognitive Services OpenAI User — grants access to OpenAI completions/embeddings
var cognitiveServicesOpenAIUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

// Azure AI Developer — grants Microsoft.MachineLearningServices/workspaces/agents/action
// Required for calling the Foundry Agents (Responses) API via hosted agents
var azureAIDeveloperRoleId = '64702f94-c441-49e6-a78b-ef80e0188fee'

resource aiAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: last(split(aiAccountId, '/'))
}

resource openAIUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiAccountId, containerAppPrincipalId, cognitiveServicesOpenAIUserRoleId)
  scope: aiAccount
  properties: {
    principalId: containerAppPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAIUserRoleId)
    principalType: 'ServicePrincipal'
  }
}

resource aiDeveloperRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiAccountId, containerAppPrincipalId, azureAIDeveloperRoleId)
  scope: aiAccount
  properties: {
    principalId: containerAppPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAIDeveloperRoleId)
    principalType: 'ServicePrincipal'
  }
}

// Cognitive Services OpenAI User for project MI — hosted agent containers call model deployments
resource projectOpenAIUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiAccountId, projectPrincipalId, cognitiveServicesOpenAIUserRoleId)
  scope: aiAccount
  properties: {
    principalId: projectPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAIUserRoleId)
    principalType: 'ServicePrincipal'
  }
}

// Azure AI Developer for project MI — hosted agent containers use the Agents API
resource projectAIDeveloperRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiAccountId, projectPrincipalId, azureAIDeveloperRoleId)
  scope: aiAccount
  properties: {
    principalId: projectPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAIDeveloperRoleId)
    principalType: 'ServicePrincipal'
  }
}

// AcrPull — grants the Foundry project MI permission to pull hosted agent images
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: last(split(acrId, '/'))
}

resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acrId, projectPrincipalId, acrPullRoleId)
  scope: acr
  properties: {
    principalId: projectPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalType: 'ServicePrincipal'
  }
}
