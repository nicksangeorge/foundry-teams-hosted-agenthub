@description('Location for the container registry')
param location string

@description('Container registry name')
param name string

param tags object = {}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
  tags: tags
}

output loginServer string = acr.properties.loginServer
output name string = acr.name
output id string = acr.id
output adminUsername string = acr.listCredentials().username
output adminPassword string = acr.listCredentials().passwords[0].value
