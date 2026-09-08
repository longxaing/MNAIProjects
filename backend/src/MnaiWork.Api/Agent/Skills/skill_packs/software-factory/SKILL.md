---
name: software-factory
description: "Create, test, and deploy React plus ASP.NET Core demo projects. Use for requests to build application code, write unit/integration/E2E tests, provision the fixed Azure infrastructure, or publish a generated project."
version: 1.2.0
category: engineering
author: MnaiWork
---

# Software Factory Skill

Use this skill whenever the user asks to create, modify, test, or deploy a software project.

## Supported target

- Frontend: React, TypeScript, Vite.
- Backend: ASP.NET Core on .NET 8.
- Tests: xUnit, WebApplicationFactory, and Playwright.
- Azure services: only App Service API, Storage Account, Cosmos DB for NoSQL, and Key Vault.
- The fixed subscription-scope orchestration template creates or updates the Generated Resource Group
   and shared Linux B1 App Service Plan before deploying project resources into that group.
- Azure tenant, subscription, resource group, region, and shared App Service Plan come from the
   Cosmos-backed DeploymentProfile and cannot be overridden by model tool arguments.

Never claim support for arbitrary stacks, Azure resources, subscriptions, resource groups, roles, or templates.

## Mandatory workflow

Execute stages in order. Never report a later stage as complete unless its tool succeeded.

0. **Runtime profile**
   - Call `get_deployment_profile` and use the Cosmos-backed Azure/build settings it returns.
   - The tool is read-only. Users edit the profile through the authenticated profile API.

1. **Requirements**
   - Convert the request into concrete user flows and acceptance criteria.
   - Ask a question only when a missing answer changes architecture or observable behavior.
2. **Architecture review**
   - Before creating a project, or before editing source for an architectural change, respond with a
      concrete architecture proposal. Do not repeat this step for implementation-only changes covered
      by the currently approved architecture.
    - Include a fenced `mermaid` flowchart showing the React frontend, ASP.NET Core API, API contract,
       managed identity, Storage, Cosmos DB, Key Vault, test layers, E2B build, and Azure publication.
         Use quoted node labels and `<br/>` for label line breaks. Never use literal `\n` sequences in
         Mermaid nodes or subgraph titles.
    - Explain the main boundaries and tradeoffs briefly, then ask the user to raise corrections or send
       exactly `APPROVE ARCHITECTURE`. End the current turn. Do not call `create_project_workspace`,
       `update_project_workspace`, or any build/deployment tool in that turn.
    - End immediately after the approval request. Do not ask the user to choose a language/framework,
       offer another stack, propose a simplified implementation, or append additional next steps.
    - If the user requests changes, revise and render the architecture again, then wait for a fresh exact
       `APPROVE ARCHITECTURE` message.
      - The exact approved Mermaid message is an implementation contract and is pinned into later LLM
         context even when older conversation turns are summarized. Implement against it; do not silently
         substitute a different topology, identity model, API boundary, or data flow.
3. **Workspace after approval**
      - `create_project_workspace` is server-gated and fails unless the latest user message after a
         Mermaid architecture proposal is exactly `APPROVE ARCHITECTURE`.
    - For every new project, call `create_project_workspace` first. Never recreate the solution,
       package lock, or test harness from scratch.
    - Modify source files with `update_project_workspace`; keep the newest returned
       `sourceArchiveFileId` as the only current revision.
    - Each update creates a complete immutable source snapshot. Group every related frontend,
       backend, and test change for the current implementation or repair into one
       `update_project_workspace` call. Never split one logical change into one call per file, and
       do not create another revision until build evidence or a new user request requires it.
    - Use `read_project_workspace` to inspect files before targeted repairs.
    - Use the fixed layout: `GeneratedApp.sln`, `src/backend`, `src/frontend`,
       `tests/backend.unit`, and `tests/backend.integration`.
4. **Implementation**
   - Implement frontend and backend together against an explicit API contract.
   - Do not place secrets or Azure credentials in generated code.
   - Build the complete user-facing workflow from the approved requirements, not a developer demo or
      API exerciser. Never expose raw author/user IDs as the primary UX when the application can derive
      identity from its authenticated user or Development test persona. Do not present a publish-only
      screen when the requested workflow also requires browsing, friendship, history, or management.
   - Give every generated frontend a deliberate, domain-specific visual direction. Do not ship an
      unstyled browser-default form. Define a compact design system in CSS with typography, spacing,
      foreground/background/surface/accent/status colors, borders, focus states, and responsive
      breakpoints. Use a purposeful font stack, strong information hierarchy, and restrained motion;
      avoid generic purple gradients, decorative blobs, oversized marketing heroes, and nested cards.
   - Make the first viewport the usable application. Provide navigation for all primary workflows,
      polished empty/loading/error/success/disabled states, accessible labels and keyboard focus, and
      touch-friendly controls. Desktop and mobile must preserve the same capabilities without overlap,
      clipped text, horizontal page scrolling, or layout shifts.
   - Match interaction patterns to the domain: feeds should be scannable, composers should make the
      primary action obvious, destructive actions need confirmation, and identity/status should be
      visible without asking users for implementation identifiers. Use icons only when their meaning is
      familiar or accompanied by an accessible label.
    - Read the current template `Program.cs` before changing startup. Preserve its health, readiness,
       Azure client, CORS, and deployment-manifest code. Add registrations and middleware directly, or
       include the complete extension-method implementation in the same source revision. Never call
       invented helpers such as `AddDefaultServices` or `UseDefaultPipeline` unless their definitions
       are present in the workspace and compile against the current template.
    - Preserve the template managed-identity contract. The backend must reference Azure.Identity,
       Azure.Storage.Blobs, Microsoft.Azure.Cosmos, and Azure.Security.KeyVault.Secrets; construct
       BlobServiceClient, CosmosClient, and SecretClient with one DefaultAzureCredential; and read only
       `Storage:ServiceUri`, `Cosmos:Endpoint`, and `KeyVault:Uri`. Never use account keys, connection
       strings, SAS tokens, client secrets, or Cosmos keys.
    - Production persistence is mandatory. Store all durable structured application data in the
       configured Cosmos database/container through the injected `CosmosClient`. For a blog, this
       includes posts, users/profile data needed by the app, friendships, visibility, comments, and
       likes when those features exist. In-memory repositories are permitted only behind an explicit
       Development/test environment branch and must never be the Production registration.
    - Use Blob Storage for durable unstructured/binary application objects such as uploaded images or
       generated files. Do not duplicate text-only records into Blob merely to use the provisioned
       service; a text-only blog uses Cosmos for its business data while Blob remains part of the fixed
       infrastructure and `/ready` dependency contract.
    - Preserve the template CORS contract: read `Frontend:Origin`, register CORS for that exact origin,
       and call `UseCors`. ARM supplies the deployed Storage static-site origin.
    - Preserve `/runtime-config.js` and the typed `window.__APP_CONFIG__.apiBaseUrl` reader. Frontend API
       calls must use this runtime value, never a build-time VITE API URL or hard-coded backend host.
    - The backend must expose an anonymous `/health` endpoint.
      - Preserve the `/ready` deployment probe protected by the current fingerprint query. It must reject
         mismatches before performing harmless authenticated reads
         against the configured appdata Blob container, Cosmos container metadata, and Key Vault secret
         metadata, then return `Deployment:Fingerprint`. Deployment waits for this probe before success.
    - The frontend must define non-watch `test`, `build`, and `test:e2e` scripts using
       Vitest and Playwright. Preserve the template lock file and use the template's exact
       `@playwright/test` version because it matches the Worker browser image.
5. **Tests**
   - Add backend unit tests for business rules and failure paths.
   - Add integration tests for HTTP, auth, validation, and persistence boundaries.
   - Add Playwright E2E tests for every acceptance criterion that is practical through the UI.
   - Treat template tests as placeholders, not product coverage. Before the first build, inspect and
      update every retained template assertion so it matches the generated product. In particular,
      replace placeholder names such as `Generated App`, sample Calculator assertions, and generic
      render-only E2E checks unless that behavior remains intentionally relevant. Product code, visible
      labels, selectors, and tests must be changed together in the same source revision.
   - Playwright must exercise the actual primary workflow, not merely assert that a heading or button
      exists. Cover navigation, a successful create/read flow, one validation or failure state, and the
      resulting visible data. Add desktop and mobile assertions for primary navigation and ensure the
      page has no horizontal overflow or uncaught browser errors.
    - The E2B runner starts the generated API on `http://127.0.0.1:5000` and Vite preview on port 4173.
       Generated code must provide Development-only local/in-memory implementations for persistence or
       external dependencies so E2E tests never require Agent Azure credentials or production resources.
6. **Validation and repair**
   - Call `build_test_project`; a disposable E2B sandbox runs restore, build, backend
     unit/integration tests, frontend Vitest, frontend build, Playwright E2E, and publish.
   - Fix product code when tests fail. Never delete, skip, or weaken a valid test merely to pass.
      - For a failed stage, inspect the latest BuildReport and define its failure signature from the failed
         stage plus primary compiler/test error codes and messages. Update all files implicated by that
         diagnostic together in one source revision, then rerun the complete pipeline.
      - Continue repairing while the failure signature changes or the error count/stage shows measurable
         progress. There is no fixed three-cycle limit. Stop when the same failure signature appears in two
         consecutive builds despite a relevant repair, when no safe targeted repair remains, or when the
         server run budget is exhausted. Report the remaining evidence and ask the user to send exactly
         `CONTINUE REPAIR <projectSlug>` to authorize another repair run against the same approved
         architecture and latest SourceZip. On that exact continuation, load this skill, read the latest
         SourceZip and BuildReport, and continue targeted repair without rendering a new architecture or
         asking for `APPROVE ARCHITECTURE` again. A feature, requirement, API, data-model, identity, or
         topology change still requires a revised architecture and fresh architecture approval.
    - A successful build returns desktop and mobile UI screenshot artifacts captured from Vite preview
       inside E2B. Inspect both screenshots before presenting them. If either looks like an unstyled
       browser-default form, omits an approved primary workflow, has excessive empty space, weak
       hierarchy, overlap, clipping, or unusable mobile composition, improve the frontend and rerun the
       pipeline instead of asking for approval. Once acceptable, show both screenshots, summarize visible
       behavior, and ask for corrections or the exact phrase `APPROVE UI`. End the turn without calling
       `preview_azure_project`.
    - If the user requests UI changes, update source, rerun the complete build/test/screenshot pipeline,
       show the new screenshots, and wait for a fresh exact `APPROVE UI` message.
    - The latest approved architecture remains the implementation contract across later turns. Styling,
       copy, accessibility, test, and bug-fix changes that preserve requirements, API contracts, data
       models, identity boundaries, Azure resources, and topology may update the latest SourceZip
       directly without rendering or approving the same architecture again. After any source change,
       rerun the complete pipeline and require a fresh `APPROVE UI` before Azure preview.
    - If a requested change affects requirements, observable workflows, API contracts, durable data
       models, identity/authorization boundaries, Azure resources, or topology, render the revised
       architecture and require a fresh `APPROVE ARCHITECTURE` before editing source.
7. **Infrastructure preview**
    - `preview_azure_project` is server-gated and fails unless the latest user message after the matching
       successful build with two screenshots is exactly `APPROVE UI`.
   - Call `preview_azure_project` only after `build_test_project` succeeds.
    - The single subscription-scope ARM what-if must include the Generated Resource Group, shared Plan,
       and project resources. Do not create foundation resources before user deployment approval.
   - Pass the `backendPackageFileId` and `frontendPackageFileId` returned by that successful build.
   - Summarize ARM what-if and ask the user to send the exact returned `DEPLOY <projectSlug>` phrase.
8. **Infrastructure deployment**
   - Never deploy in the same user turn as preview.
   - Call `deploy_azure_project` only after the subsequent exact approval phrase.
   - Formal deployment runs the same fixed orchestration template in Incremental mode: create/update
     Generated RG, create/update the shared Plan, then create/update the approved project resources.
9. **Code publication and cloud E2E**
   - Publish only the tested immutable artifacts.
   - Deployment replaces only the tested frontend package's runtime-config.js placeholder with the
     trusted ARM `appUrl`; never modify generated source or rebuild after infrastructure deployment.
   - Run health checks and cloud E2E before reporting success.
    - Automatic rollback is not available on the fixed B1 App Service plan. On publication or health
       verification failure, stop, report the failed stage, and preserve the previous deployment record
       and package artifacts for an explicitly approved recovery deployment.
10. **Resource inspection**
   - Use `list_azure_project_resources` to inspect supported resources in the profile Generated RG.
   - Use `get_azure_project_resource` only with an id returned by the list tool.
   - Deployment URLs and hashes are saved in a deployment-record JSON artifact after success.

## Existing project iteration

- A deployed project can continue to evolve in the same conversation thread. Treat a request to fix,
   change, or add a feature as a new controlled revision, not as a new unrelated project.
- Call `list_my_files` and select the newest `SourceZip` whose filename matches `<projectSlug>-source.zip`.
   Use its id with `read_project_workspace`; never reconstruct deployed source from generated packages.
- Convert the requested delta into updated acceptance criteria and render a revised Mermaid architecture
   that includes both retained behavior and the proposed change. Wait for a new exact
   `APPROVE ARCHITECTURE`; `update_project_workspace` is server-gated by this approval.
- Update only the latest source revision, then rerun the complete backend unit/integration, frontend
   Vitest/build, Playwright E2E, publish, and desktop/mobile screenshot pipeline. Never reuse an old
   package or screenshot after source changes.
- Wait for a fresh exact `APPROVE UI`, run a new ARM what-if, and request a new exact
   `DEPLOY <projectSlug>`. Reusing the same slug updates deterministic resources in Incremental mode and
   publishes the new tested artifacts; package hashes create a new deployment fingerprint.
- Iteration is currently thread-scoped. A different conversation cannot access the original SourceZip.
   Do not claim cross-thread project recovery until a long-lived Project/Revision repository exists.

## Tool availability rule

A workflow stage may execute only when the corresponding server tool is visible and succeeds.

The current server release exposes immutable source workspace tools, isolated E2B build/test execution,
and Azure preview/deployment tools. Full cloud E2E and automatic rollback are not implemented yet.
Never ask the user to build or upload deployment ZIP files: generate source with the workspace tools
and obtain both packages from `build_test_project`. Never simulate build, test, or deployment results.

## Security invariants

- Generated build/test processes receive no Agent Azure tokens, Key Vault values, or Cosmos credentials.
- Builds run in one disposable secure E2B sandbox per invocation. The sandbox is deleted after success,
   failure, cancellation, or timeout.
- The model cannot provide an ARM template, role, or resource type. Deployment target and build
   settings come only from the Cosmos-backed DeploymentProfile, never from model tool arguments.
- Infrastructure deployment requires a successful matching what-if and a persisted exact user approval message.
- Never expose tokens, SAS values, publish profiles, connection strings, or secret values.

## Completion report

Report each stage separately as one of: `completed`, `failed`, `blocked`, or `not available`.
Include build/test evidence and deployed URLs only when returned by trusted tools.
