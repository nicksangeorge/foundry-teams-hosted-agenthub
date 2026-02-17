@description('Location — use global for Bot Service')
param location string

@description('Bot service name')
param name string

@secure()
param botAppId string

param tenantId string
param botEndpoint string

param tags object = {}

resource bot 'Microsoft.BotService/botServices@2023-09-15-preview' = {
  name: name
  location: location
  sku: {
    name: 'F0'
  }
  kind: 'azurebot'
  properties: {
    displayName: 'Contoso Agent Hub'
    endpoint: botEndpoint
    msaAppId: botAppId
    msaAppTenantId: tenantId
    msaAppType: 'SingleTenant'
  }
  tags: tags
}

resource teamsChannel 'Microsoft.BotService/botServices/channels@2023-09-15-preview' = {
  parent: bot
  name: 'MsTeamsChannel'
  location: location
  properties: {
    channelName: 'MsTeamsChannel'
    properties: {
      isEnabled: true
    }
  }
}

output botName string = bot.name
