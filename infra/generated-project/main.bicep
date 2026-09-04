@description('Lowercase project slug: letters, numbers, and hyphens only.')
@minLength(3)
@maxLength(24)
param projectSlug string

@description('Azure region. Must match the pre-created App Service Plan region.')
param location string = resourceGroup().location

@description('Name of the pre-created shared Linux App Service Plan.')
param appServicePlanName string = 'asp-mnaiwork-generated-demo'

@description('Object/principal ID of the platform deployment managed identity.')
param deploymentPrincipalId string

@description('Cosmos DB database name.')
param cosmosDatabaseName string = 'app'

@description('Cosmos DB container name.')
param cosmosContainerName string = 'items'

@description('Anonymous backend health endpoint used after publication.')
param healthCheckPath string = '/health'

@description('Server-generated fingerprint binding approved packages and deployment settings.')
param deploymentFingerprint string

var suffix = uniqueString(resourceGroup().id, projectSlug)
var compactSlug = replace(projectSlug, '-', '')
var storageName = take('${compactSlug}${suffix}', 24)
var keyVaultName = take('kv-${projectSlug}-${suffix}', 24)
var cosmosName = take('cosmos-${projectSlug}-${suffix}', 44)
var appName = take('api-${projectSlug}-${suffix}', 60)

resource appServicePlan 'Microsoft.Web/serverfarms@2024-11-01' existing = {
  name: appServicePlanName
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource webContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: '$web'
  properties: {
    publicAccess: 'None'
  }
}

resource appDataContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'appdata'
  properties: {
    publicAccess: 'None'
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: cosmosName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmos
  name: cosmosDatabaseName
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
  }
}

resource cosmosContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: cosmosContainerName
  properties: {
    resource: {
      id: cosmosContainerName
      partitionKey: {
        paths: [
          '/partitionKey'
        ]
        kind: 'Hash'
        version: 2
      }
    }
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: false
    publicNetworkAccess: 'Enabled'
  }
}

resource apiApp 'Microsoft.Web/sites@2024-11-01' = {
  name: appName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      alwaysOn: false
      healthCheckPath: healthCheckPath
      appSettings: [
        {
          name: 'Cosmos__Endpoint'
          value: cosmos.properties.documentEndpoint
        }
        {
          name: 'Cosmos__Database'
          value: cosmosDatabaseName
        }
        {
          name: 'Cosmos__Container'
          value: cosmosContainerName
        }
        {
          name: 'Storage__ServiceUri'
          value: storage.properties.primaryEndpoints.blob
        }
        {
          name: 'Storage__Container'
          value: appDataContainer.name
        }
        {
          name: 'KeyVault__Uri'
          value: keyVault.properties.vaultUri
        }
        {
          name: 'Frontend__Origin'
          value: storage.properties.primaryEndpoints.web
        }
        {
          name: 'Deployment__Fingerprint'
          value: deploymentFingerprint
        }
        {
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'false'
        }
      ]
    }
  }
}

resource appStorageDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appDataContainer.id, apiApp.id, 'app-storage-data-contributor')
  scope: appDataContainer
  properties: {
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    )
  }
}

resource deployStorageDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, deploymentPrincipalId, 'deploy-storage-data-contributor')
  scope: storage
  properties: {
    principalId: deploymentPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    )
  }
}

resource appKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, apiApp.id, 'app-key-vault-secrets-user')
  scope: keyVault
  properties: {
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
  }
}

resource appCosmosDataContributor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, apiApp.id, 'app-cosmos-data-contributor')
  properties: {
    principalId: apiApp.identity.principalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: cosmos.id
  }
}

output appName string = apiApp.name
output appUrl string = 'https://${apiApp.properties.defaultHostName}'
output appPrincipalId string = apiApp.identity.principalId
output storageAccountName string = storage.name
output storageBlobEndpoint string = storage.properties.primaryEndpoints.blob
output storageWebEndpoint string = storage.properties.primaryEndpoints.web
output cosmosAccountName string = cosmos.name
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
