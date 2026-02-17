@description('Location for the container app')
param location string

@description('Container app name')
param name string

@description('Container Apps Environment name')
param environmentName string

@secure()
param botAppId string

@secure()
param botAppSecret string

param tenantId string
param foundryProjectEndpoint string
param opsAgentName string
param menuAgentName string
param orchestratorAgentName string
param modelDeployment string
param acrLoginServer string
@secure()
param acrAdminUsername string
@secure()
param acrAdminPassword string

param tags object = {}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: 'log-${name}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: tags
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
  tags: tags
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  tags: union(tags, { 'azd-service-name': 'bot' })
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: [
        {
          server: acrLoginServer
          username: acrAdminUsername
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: [
        { name: 'bot-app-secret', value: botAppSecret }
        { name: 'acr-password', value: acrAdminPassword }
      ]
    }
    template: {
      containers: [
        {
          name: 'bot'
          // Placeholder image for initial provisioning. `azd deploy` pushes the real bot image to ACR and updates the container.
          image: 'mcr.microsoft.com/k8se/quickstart:latest'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'Connections__ServiceConnection__Settings__AuthType', value: 'ClientSecret' }
            { name: 'Connections__ServiceConnection__Settings__AuthorityEndpoint', value: '${environment().authentication.loginEndpoint}${tenantId}' }
            { name: 'Connections__ServiceConnection__Settings__ClientId', value: botAppId }
            { name: 'Connections__ServiceConnection__Settings__ClientSecret', secretRef: 'bot-app-secret' }
            { name: 'Connections__ServiceConnection__Settings__TenantId', value: tenantId }
            { name: 'Connections__ServiceConnection__Settings__Scopes__0', value: 'https://api.botframework.com/.default' }
            { name: 'TokenValidation__Audiences__0', value: botAppId }
            { name: 'TokenValidation__TenantId', value: tenantId }
            { name: 'Foundry__ProjectEndpoint', value: foundryProjectEndpoint }
            { name: 'Foundry__OpsAgentName', value: opsAgentName }
            { name: 'Foundry__MenuAgentName', value: menuAgentName }
            { name: 'Foundry__OrchestratorAgentName', value: orchestratorAgentName }
            { name: 'Foundry__OpsAgentVersion', value: '1' }
            { name: 'Foundry__MenuAgentVersion', value: '1' }
            { name: 'Foundry__OrchestratorAgentVersion', value: '1' }
            { name: 'Foundry__VisionDeployment', value: modelDeployment }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

output fqdn string = app.properties.configuration.ingress.fqdn
output name string = app.name
output principalId string = app.identity.principalId
