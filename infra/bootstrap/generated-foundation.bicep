@description('Azure region for the shared App Service Plan.')
param location string

@description('Shared Windows App Service Plan name.')
param appServicePlanName string

@description('Object/principal ID of the platform system-assigned managed identity.')
param deploymentPrincipalId string

var roleAssignmentCondition = '''
((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {ba92f5b4-2d11-453d-a403-e96b0029c9fe, 4633458b-17de-408a-b874-0445c86b69e6} AND @Request[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {ba92f5b4-2d11-453d-a403-e96b0029c9fe, 4633458b-17de-408a-b874-0445c86b69e6} AND @Resource[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'}))
'''

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

resource deploymentRbacAdmin 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, deploymentPrincipalId, 'generated-rbac-admin')
  properties: {
    principalId: deploymentPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'f58310d9-a9f6-439a-9e8d-f62e7b41a168'
    )
    condition: roleAssignmentCondition
    conditionVersion: '2.0'
  }
}

output appServicePlanId string = appServicePlan.id
