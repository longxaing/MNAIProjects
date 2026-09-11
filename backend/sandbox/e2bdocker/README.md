# MnaiWork E2B Sandbox Template

This template follows the layout used by `C:\src\Societas\backend\sandbox\e2bdocker`, but only
installs the software-factory toolchain:

- .NET 8 SDK
- Node.js and npm from the official Playwright image
- Playwright 1.62.1
- Headless Chromium and required Linux packages
- Git, zip/unzip, jq, and process utilities
- A fixed ASP.NET Core build runner on port 3000

The image is based on the official Playwright 1.62.1 image, so each sandbox can run `npm ci` and
`npm run test:e2e` without downloading Chromium again. The generated project still pins
`@playwright/test` to 1.62.1.

## Prerequisites

1. Create or select an E2B project.
2. Install Docker Desktop and start its Linux engine.
3. Install dependencies in this directory:

```powershell
npm install
```

4. Authenticate the CLI:

```powershell
npm run login
```

## Build

Create or rebuild the `mnaiwork-software-factory` template from the checked-in Dockerfile:

```powershell
npm run build
```

The script uses `e2b template create`, explicitly selects `e2b.Dockerfile`, requests 2 vCPUs and
4 GB memory, starts the runner, waits for `/health`, and snapshots the running process. Store both
`E2B--ApiKey` and `E2B--TemplateId` in Key Vault.

## Local verification

```powershell
npm run local:up
npm run local:verify
npm run local:build-verify
npm run local:down
```

The local container mounts `./workspace` at `/home/user/workspace`. Verification checks the toolchain
and the runner health endpoint. `local:build-verify` sends the checked-in fixture through the Linux
runner, starts the generated API and Vite preview, and verifies all build/test stages plus
desktop/mobile PNG output. Docker Desktop must be running.

### Local and Production dependencies

The generated project template selects dependencies explicitly with `IsDevelopment()` in Program.cs:

| Dependency | Development / sandbox | Production and other environments |
| --- | --- | --- |
| Example note repository | InMemoryNoteRepository | CosmosNoteRepository |
| Business file storage | InMemoryAppFileStore | BlobAppFileStore |
| Secret values | LocalAppSecrets, from non-sensitive DevelopmentSecrets configuration | KeyVaultAppSecrets |
| Readiness | LocalDependencyCheck, no Azure calls | AzureDependencyCheck, real Blob/Cosmos/Key Vault metadata reads |

Development does not register Azure SDK clients or a TokenCredential. Real HTTP handlers and business
logic use dependency interfaces; only external I/O is substituted. Missing local test values fail
explicitly. Local memory data is isolated to one app instance and lost on restart. This is not an
Azure emulator and cannot validate cloud RBAC or service behavior. Production never falls back to
local stores when configuration or authentication fails. The fingerprint is checked before resolving
readiness dependencies in every environment.

The notes repository is a starter example, not a complete production domain/auth design. Its fixture
HTTP endpoints remain Development-only. Generated products must replace the example with their own
domain repository and authorization while preserving both environment implementations and tests.
Do not add platform Azure credentials to E2B or remove Production clients to fix sandbox E2E.

Template and Skill changes ship with the platform API and apply to newly created workspaces. Existing
SourceZip revisions require a source update and rebuild; they are not migrated automatically. These
dependency changes do not modify the runner or require a new E2B image by themselves. The independent
browser gate below still requires a runner image containing that gate.

### Primary workflow gate

The runner now requires `src/frontend/acceptance.json` (version 1); the checked-in fixture contains
an example. It independently fills labelled text inputs, submits to the actual local API, navigates
to a fresh document, and verifies the unique submitted value in both a GET JSON response and visible
page content on desktop and mobile. Missing contracts, no-op buttons, API errors, and lost data fail
the build before publish, even when generated Playwright tests pass. The fixture's notes API is only
a Development test harness and must be replaced by the generated product's workflow.

This version supports single-page text-input create/read flows only. Login setup, multi-step flows,
uploads, and read-only apps need a future contract extension, not a fake write flow or bypass.
Local Development checks do not prove Production Cosmos serialization, durable storage, authorization,
or Windows hosting. Product-specific tests and cloud verification remain necessary.

The verifier is embedded in MnaiWork.BuildExecution.dll. Rebuild the E2B template and update the
configured TemplateId when deploying this change; publishing only the API ZIP leaves the old sandbox
runner unchanged. Existing sandboxes must expire or be replaced before the new gate runs. No Linux/E2B
verification is implied by a successful local Windows test.

## Sandbox verification

After building the E2B template, the Agent creates a secure sandbox scoped to one user, conversation,
and project. Repair builds can reuse that sandbox during the configured E2B lifetime so NuGet and npm
download caches remain warm; reuse stops early to leave time before provider expiry. Each request builds
the supplied immutable SourceZip in a clean temporary workspace.
If the build endpoint returns HTTP 404/410 or 500/502/503/504, the platform disposes the old sandbox
and retries the same SourceZip once in a fresh sandbox, within the original total timeout. A second
HTTP failure is surfaced with retry exhaustion and response details; HTTP 500 alone does not establish
a provider outage. Structured compile/test failures are returned for source repair, not blindly retried;
400/401/403 and cancellation are not retried by this policy. Sandbox creation failures are not retried.
This retry policy runs in the platform backend and needs an API update, not an E2B template rebuild.
For manual toolchain verification inside a sandbox, run:

```bash
verify-toolchain
dotnet --list-sdks
node --version
playwright --version
```

`BuildExecutor` sends the source archive and non-secret build settings to the runner through an E2B
traffic-token-protected URL. It deletes expired sandboxes and all cached sandboxes during graceful API
shutdown. Agent Azure tokens, Key Vault values, Cosmos credentials, and the E2B API key are never passed
into the sandbox.