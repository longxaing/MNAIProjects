targetScope = 'subscription'

param generatedResourceGroupName string
param location string = 'canadacentral'
param cosmosLocation string = location
param appServicePlanName string

@description('Existing Windows Plan resource ID. This template never creates or updates a Plan.')
@minLength(1)
param existingAppServicePlanResourceId string

param deploymentPrincipalId string
param projectSlug string
param cosmosDatabaseName string
param cosmosContainerName string
param healthCheckPath string
param deploymentFingerprint string

resource generatedResourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: generatedResourceGroupName
  location: location
}

var deploymentSuffix = take(uniqueString(deployment().name), 8)

module project '../generated-project/main.bicep' = {
  name: 'generated-project-${projectSlug}-${deploymentSuffix}'
  scope: generatedResourceGroup
  params: {
    projectSlug: projectSlug
    location: location
    cosmosLocation: cosmosLocation
    appServicePlanName: appServicePlanName
    deploymentPrincipalId: deploymentPrincipalId
    existingAppServicePlanResourceId: existingAppServicePlanResourceId
    cosmosDatabaseName: cosmosDatabaseName
    cosmosContainerName: cosmosContainerName
    healthCheckPath: healthCheckPath
    deploymentFingerprint: deploymentFingerprint
  }
}

output generatedResourceGroupId string = generatedResourceGroup.id
output appServicePlanId string = existingAppServicePlanResourceId
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