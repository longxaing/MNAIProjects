targetScope = 'subscription'

@description('Resource group used only for generated projects.')
param generatedResourceGroupName string

@description('Azure region for the resource group and all generated resources.')
param location string

@description('Shared Linux App Service Plan name.')
param appServicePlanName string

@description('Object/principal ID of the platform deployment managed identity.')
param deploymentPrincipalId string

@description('Lowercase project slug: letters, numbers, and hyphens only.')
param projectSlug string

@description('Cosmos DB database name.')
param cosmosDatabaseName string

@description('Cosmos DB container name.')
param cosmosContainerName string

@description('Anonymous backend health endpoint used after publication.')
param healthCheckPath string

@description('Server-generated fingerprint binding approved packages and deployment settings.')
param deploymentFingerprint string

resource generatedResourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: generatedResourceGroupName
  location: location
}

var deploymentSuffix = take(uniqueString(deployment().name), 8)

module foundation './foundation.bicep' = {
  name: 'generated-foundation-${deploymentSuffix}'
  scope: generatedResourceGroup
  params: {
    location: location
    appServicePlanName: appServicePlanName
  }
}

module project '../generated-project/main.bicep' = {
  name: 'generated-project-${projectSlug}-${deploymentSuffix}'
  scope: generatedResourceGroup
  params: {
    projectSlug: projectSlug
    location: location
    appServicePlanName: appServicePlanName
    deploymentPrincipalId: deploymentPrincipalId
    cosmosDatabaseName: cosmosDatabaseName
    cosmosContainerName: cosmosContainerName
    healthCheckPath: healthCheckPath
    deploymentFingerprint: deploymentFingerprint
  }
  dependsOn: [
    foundation
  ]
}

output generatedResourceGroupId string = generatedResourceGroup.id
output appServicePlanId string = foundation.outputs.appServicePlanId
output appName string = project.outputs.appName
output appUrl string = project.outputs.appUrl
output appPrincipalId string = project.outputs.appPrincipalId
output storageAccountName string = project.outputs.storageAccountName
output storageBlobEndpoint string = project.outputs.storageBlobEndpoint
output storageWebEndpoint string = project.outputs.storageWebEndpoint
output cosmosAccountName string = project.outputs.cosmosAccountName
output cosmosEndpoint string = project.outputs.cosmosEndpoint
output keyVaultName string = project.outputs.keyVaultName
output keyVaultUri string = project.outputs.keyVaultUri