  @description('Location for the AI resources')
param location string

@description('Microsoft Foundry resource name (CognitiveServices account)')
param name string

@description('Microsoft Foundry project name (child of the account)')
param projectName string

@description('Model deployment name')
param modelDeployment string

param tags object = {}

// Microsoft Foundry resource — CognitiveServices account with kind AIServices.
// allowProjectManagement: true is required for the new Microsoft Foundry resource model (Nov 2025+).
// Ref: https://learn.microsoft.com/azure/ai-foundry/how-to/create-resource-template
// Ref: https://github.com/azure-ai-foundry/foundry-samples/tree/main/infrastructure/infrastructure-setup-bicep/00-basic
resource aiAccount 'Microsoft.CognitiveServices/accounts@2025-04-01-preview' = {
  name: name
  location: location
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    allowProjectManagement: true
    customSubDomainName: name
    publicNetworkAccess: 'Enabled'
    // Must be false — Hosted Agents managed environment provisioning uses key-based auth
    // internally. The MCAPS CognitiveServices_LocalAuth_Modify policy is exempted at the RG level.
    disableLocalAuth: false
    networkAcls: {
      defaultAction: 'Allow'
      ipRules: []
      virtualNetworkRules: []
    }
  }
  tags: tags
}

// Microsoft Foundry project — child resource under the account.
// Groups agent deployments, files, and connections for a single use case.
resource project 'Microsoft.CognitiveServices/accounts/projects@2025-04-01-preview' = {
  parent: aiAccount
  name: projectName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {}
}

// Model deployment on the Microsoft Foundry resource
resource modelDeploy 'Microsoft.CognitiveServices/accounts/deployments@2025-04-01-preview' = {
  parent: aiAccount
  name: modelDeployment
  sku: {
    name: 'GlobalStandard'
    capacity: 30
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelDeployment
      version: '2024-07-18'
    }
  }
}

// Account-level capability host — enables hosted agents on the Microsoft Foundry resource.
// enablePublicHostingEnvironment is required for hosted agent container provisioning.
// NOTE: Do NOT create a project-level capability host. The working deployment
// (rg-yum-fresh-foundry) only has an account-level host. A project-level host
// triggers ML Hub auto-creation, which gets locked down by the MCAPS
// AIFoundryHub_PublicNetwork_Modify governance policy (forces publicNetworkAccess=Disabled),
// causing managed environment provisioning to time out.
// Ref: https://learn.microsoft.com/azure/ai-foundry/agents/concepts/hosted-agents
// Capability host is deployed via the postprovision hook (REST API with 2025-10-01-preview).
// The 2025-04-01-preview Bicep schema does not support enablePublicHostingEnvironment,
// and that property is required for hosted agent container provisioning.
// See scripts/postprovision.ps1 for the REST-based creation.

// New Microsoft Foundry endpoint format: https://<name>.services.ai.azure.com/api/projects/<project>
// NOTE: aiAccount.properties.endpoint returns the cognitiveservices.azure.com domain,
// but the Microsoft Foundry Agents API requires the services.ai.azure.com domain.
output projectEndpoint string = 'https://${name}.services.ai.azure.com/api/projects/${project.name}'
output aiEndpoint string = 'https://${name}.services.ai.azure.com/'
output aiAccountName string = aiAccount.name
output projectName string = project.name
output aiAccountId string = aiAccount.id
output projectPrincipalId string = project.identity.principalId
