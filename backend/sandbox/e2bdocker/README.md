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

## Sandbox verification

After building the E2B template, the Agent creates a secure sandbox scoped to one user, conversation,
and project. Repair builds can reuse that sandbox during the configured E2B lifetime so NuGet and npm
download caches remain warm; reuse stops early to leave time before provider expiry. Each request builds
the supplied immutable SourceZip in a clean temporary workspace.
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