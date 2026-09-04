@description('Azure region for the shared App Service Plan.')
param location string

@description('Shared Linux App Service Plan name.')
param appServicePlanName string

resource appServicePlan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: appServicePlanName
  location: location
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
    size: 'B1'
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

output appServicePlanId string = appServicePlan.id