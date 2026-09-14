@description('Azure region for the shared App Service Plan.')
param location string

@description('Shared Windows App Service Plan name.')
param appServicePlanName string

resource appServicePlan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: appServicePlanName
  location: location
  kind: 'app'
  sku: {
    name: 'B1'
    tier: 'Basic'
    size: 'B1'
    capacity: 1
  }
  properties: {
    reserved: false
  }
}

output appServicePlanId string = appServicePlan.id