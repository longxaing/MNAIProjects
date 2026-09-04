targetScope = 'subscription'

@description('Resource group used only for generated demo projects.')
param generatedResourceGroupName string = 'rg-mnaiwork-generated-demo'

@description('Azure region for the resource group and shared App Service Plan.')
param location string = 'eastus2'

@description('Shared Linux App Service Plan name.')
param appServicePlanName string = 'asp-mnaiwork-generated-demo'

@description('Object/principal ID of the platform system-assigned managed identity.')
param deploymentPrincipalId string

resource generatedResourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: generatedResourceGroupName
  location: location
}

resource generatedProjectDeployerRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, 'mnai-generated-project-deployer')
  properties: {
    roleName: 'Mnai Generated Project Deployer'
    description: 'Deploys only App Service, Storage, Cosmos DB for NoSQL, and Key Vault resources.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.Resources/deployments/*'
          'Microsoft.Resources/subscriptions/resourceGroups/read'
          'Microsoft.Resources/subscriptions/resourceGroups/write'
          'Microsoft.Web/sites/*'
          'Microsoft.Web/serverFarms/read'
          'Microsoft.Web/serverFarms/write'
          'Microsoft.Web/serverFarms/join/action'
          'Microsoft.Storage/storageAccounts/*'
          'Microsoft.DocumentDB/databaseAccounts/*'
          'Microsoft.KeyVault/vaults/*'
        ]
        notActions: [
          'Microsoft.Storage/storageAccounts/listKeys/action'
          'Microsoft.Storage/storageAccounts/listAccountSas/action'
          'Microsoft.Storage/storageAccounts/listServiceSas/action'
          'Microsoft.Storage/storageAccounts/regeneratekey/action'
          'Microsoft.DocumentDB/databaseAccounts/listKeys/action'
          'Microsoft.DocumentDB/databaseAccounts/readonlykeys/action'
          'Microsoft.DocumentDB/databaseAccounts/regenerateKey/action'
          'Microsoft.DocumentDB/databaseAccounts/listConnectionStrings/action'
        ]
        dataActions: []
        notDataActions: []
      }
    ]
    assignableScopes: [
      subscription().id
    ]
  }
}

resource deploymentResourceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, deploymentPrincipalId, generatedProjectDeployerRole.id)
  properties: {
    principalId: deploymentPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: generatedProjectDeployerRole.id
  }
}

module generatedFoundation './generated-foundation.bicep' = {
  name: 'generated-foundation'
  scope: generatedResourceGroup
  params: {
    location: location
    appServicePlanName: appServicePlanName
    deploymentPrincipalId: deploymentPrincipalId
  }
}

output generatedResourceGroupId string = generatedResourceGroup.id
output appServicePlanId string = generatedFoundation.outputs.appServicePlanId
