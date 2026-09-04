# Azure Demo Infrastructure

This folder contains the fixed infrastructure used by the software-factory demo.
The deployment path creates only these generated-project services:

- Azure App Service API
- Azure Storage account
- Azure Cosmos DB for NoSQL
- Azure Key Vault

The Agent uses a fixed subscription-scope orchestration template. Its ARM what-if and deployment
cover the generated-project resource group, shared Linux B1 App Service Plan, and project resources
in one operation. Users do not need to pre-create the group or plan.

Deployments are idempotent. Project resource names are deterministic for a resource group and project
slug, and ARM Incremental mode creates missing resources or updates existing resources in place. ARM
what-if reports whether the group, plan, and project resources will be created, modified, or unchanged.

## 1. Enable and authorize the Agent identity

```powershell
$agentResourceGroup = "rg-mnaiwork-agent-demo"
$agentAppName = "<agent-app-service-name>"

$deploymentPrincipalId = az webapp identity assign `
  --resource-group $agentResourceGroup `
  --name $agentAppName `
  --query principalId `
  --output tsv
```

The deployment identity needs subscription-scope permission to create resource groups, deployments,
App Service plans, and the four allowed project services. For a simple test, assign `Contributor` at
subscription scope. It also needs `Role Based Access Control Administrator` constrained to assigning
only `Storage Blob Data Contributor` and `Key Vault Secrets User` to service principals.

A user-assigned identity such as `MNAI-MI` is available only to Azure resources that attach it. When
the Agent API runs on App Service, attach that identity and set `AZURE_CLIENT_ID` to its client ID;
`deploymentPrincipalId` must be its object/principal ID. A local process cannot impersonate that MI.
For a full local deployment test, use an explicitly authorized service principal through
`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, and `AZURE_CLIENT_SECRET`, and set `deploymentPrincipalId` to that
service principal's object ID. Otherwise test locally through UI screenshots and run Azure publication
from the deployed Agent App Service.

## 2. Optional least-privilege bootstrap

This bootstrap is not required when the deployment identity already has subscription Contributor plus
constrained RBAC Administrator. It remains available for administrators who prefer replacing broad
Contributor access with the repository's custom four-service deployment role.

```powershell
$subscriptionId = az account show --query id --output tsv
$tenantId = az account show --query tenantId --output tsv
$location = "eastus2"

az deployment sub create `
  --name "mnai-generated-bootstrap" `
  --location $location `
  --template-file ".\infra\bootstrap\main.bicep" `
  --parameters `
    generatedResourceGroupName="rg-mnaiwork-generated-demo" `
    appServicePlanName="asp-mnaiwork-generated-demo" `
    location=$location `
    deploymentPrincipalId=$deploymentPrincipalId
```

The optional bootstrap creates:

- `rg-mnaiwork-generated-demo`
- `asp-mnaiwork-generated-demo` (Linux B1)
- a custom deployment role limited to App Service, Storage, Cosmos DB, and Key Vault
- constrained role-assignment permission for the approved Storage and Key Vault data roles

## 3. Configure the software-factory profile

The software-factory runtime settings are stored in the single Cosmos item
`deploymentProfiles/default`. Every authenticated user can view it; updates require the API app role
`MnaiWork.DeploymentAdmin`:

```http
GET /api/deployment-profile
PUT /api/deployment-profile
If-Match: <ETag returned by GET>
```

Define `MnaiWork.DeploymentAdmin` on the API app registration and assign it only to trusted deployment
administrators. The local Development auth handler receives this role automatically.

Before starting the Agent API for the first time, create this item in Cosmos Data Explorer under
the `deploymentProfiles` container. Replace the four GUID placeholders with real values:

```json
{
  "id": "default",
  "azureProvisioningEnabled": true,
  "tenantId": "<tenant-guid>",
  "subscriptionId": "<subscription-guid>",
  "generatedResourceGroup": "rg-mnaiwork-generated-demo",
  "location": "eastus2",
  "appServicePlanName": "asp-mnaiwork-generated-demo",
  "deploymentPrincipalId": "<agent-sami-principal-guid>",
  "azureTimeoutMinutes": 30,
  "buildExecutionEnabled": true,
  "maxConcurrentBuilds": 1,
  "commandTimeoutMinutes": 10,
  "totalTimeoutMinutes": 30,
  "playwrightVersion": "1.62.1",
  "azureAdTenantId": "<tenant-guid>",
  "version": 1,
  "updatedBy": "bootstrap",
  "updatedAt": "2026-08-21T00:00:00Z"
}
```

There is no Key Vault or App Configuration fallback for these fields. If the item is missing, API
startup fails with an explicit instruction to create `deploymentProfiles/default`. The item contains
Azure provisioning enabled state, tenant/subscription, Generated RG, location, shared plan, Agent
SAMI principal id, Azure timeout, build enabled state, concurrency/timeouts, Playwright version,
and Azure AD tenant. `get_deployment_profile` exposes the same item read-only to the Agent. A changed
Azure AD tenant returns `restartRequired=true`; restart the Agent API so authentication is rebuilt.
Cosmos/OpenAI/Storage credentials and other existing secrets remain in Key Vault and are unaffected.

Builds run in disposable E2B sandboxes; the Agent API host no longer needs the .NET SDK, Node.js,
npm, or browser binaries. Store the E2B API key as `E2B--ApiKey` and template ID as
`E2B--TemplateId` in Key Vault. Neither value is added to Cosmos or passed into a sandbox. Build and
publish the template in `backend/sandbox/e2bdocker` before enabling `BuildExecution`.

The profile Azure AD tenant must be a concrete tenant id and match the provisioning tenant. Shared
authorities such as `common` are rejected outside Development. Configure the frontend build with
`VITE_AAD_TENANT=<same-tenant-id>`.

## 4. Preview and deploy through the Agent

Ask the Agent to create, test, and deploy an application. Before source changes, it renders a Mermaid
architecture and waits for the exact user message `APPROVE ARCHITECTURE`. The approved diagram is
pinned into later LLM context and acts as the implementation contract.

The Agent then writes immutable source ZIP revisions, builds and tests the current revision in E2B,
repairs failures up to three times, and obtains deployment packages automatically. E2B also renders
desktop and mobile screenshots. The chat displays both images, and ARM preview remains locked until
the user sends exactly `APPROVE UI`. After UI approval, ask the Agent to preview deployment:

```text
Preview the Azure infrastructure for project demo-one using Cosmos database app and container items.
```

The Agent calls `preview_azure_project`, summarizes the subscription-scope ARM what-if, and returns an
exact approval phrase. The preview includes the Generated Resource Group, shared Plan, and all project
resources; it does not create them before approval.
After reviewing the result, send that phrase as a new message with no additional text:

```text
DEPLOY demo-one
```

The Agent can then call `deploy_azure_project`. The tool verifies conversation ownership, tenant,
the exact latest user message, that both packages came from the same successful Worker build,
package hashes, and a fingerprint of the template, target environment, and deployment arguments.
It publishes the backend ZIP to App Service, uploads the frontend ZIP to Storage static website
hosting, replaces the tested `runtime-config.js` placeholder with the trusted ARM `appUrl`, and
verifies both endpoints. ARM injects the Storage static-site origin for CORS. The generated API uses
its system-assigned managed identity for Blob, Cosmos data-plane, and Key Vault secret access.

The approved deployment fingerprint is injected into both the API and `runtime-config.js`. Publication
is successful only when `/health`, `/ready`, and the frontend runtime config return that exact value.
`/ready` also performs harmless reads against the appdata Blob container, Cosmos container metadata,
and Key Vault secret metadata. First-deployment publication retries expected 401/403/404/409 responses
while new resources and RBAC assignments propagate.

On success the tool returns and records these outputs:

- `appUrl`: deployed ASP.NET Core API URL
- `frontendUrl`: verified Storage static website URL
- `storageBlobEndpoint`: Blob data endpoint
- `cosmosEndpoint`: Cosmos DB for NoSQL endpoint
- `keyVaultUri`: Key Vault URI

The complete output, deployment name, package hashes, subscription, resource group, and timestamp
are persisted as a downloadable `deployment-record.json` artifact attached to the tool message. The
tool message itself is stored in Cosmos with the conversation. Deleting the conversation also deletes
its artifacts; a separate long-lived Project/Deployment repository is still a future enhancement.

The fixed B1 App Service Plan does not support deployment slots, so backend ZIP publication is not an
atomic traffic swap and automatic rollback is not claimed. Frontend assets and runtime config are
uploaded before `index.html` to keep the previous entry point active until the candidate is complete.
Deployment records include package artifact IDs and arguments for an explicitly approved recovery.

## 5. Existing project iteration

Within the same conversation, users can request fixes or new features after deployment. The Agent:

1. Calls `list_my_files` and selects the newest `<projectSlug>-source.zip` revision.
2. Reads the project, proposes a revised Mermaid architecture, and waits for a new
  `APPROVE ARCHITECTURE`.
3. Updates that immutable source revision and reruns every test, package, and UI screenshot stage.
4. Waits for a new `APPROVE UI`, runs a new ARM what-if, and requests `DEPLOY <projectSlug>` again.
5. Reuses the slug so Incremental ARM updates deterministic resources and publishes packages bound to
  a new deployment fingerprint.

Iteration is currently limited to the original thread because SourceZip ownership is conversation-scoped.
Cross-thread continuation requires the future long-lived Project/Revision repository.

Use `list_azure_project_resources` to list supported resources in the fixed Generated Resource Group,
then `get_azure_project_resource` with an exact returned resource id to inspect ARM details. These
tools cannot select another subscription/resource group and redact secret-like fields.

The target subscription, resource group, and App Service Plan come from the Cosmos profile and are
not accepted as model tool arguments. Resource types remain fixed by the embedded reviewed template.
