---
name: software-factory
description: "Design polished, premium React interfaces with working ASP.NET Core backends, then test and deploy them. Use for application creation, UI design and refinement, unit/integration/E2E tests, fixed Azure infrastructure, and generated project publication."
version: 1.4.0
category: engineering
author: MnaiWork
---

# Software Factory Skill

Use this skill whenever the user asks to create, modify, test, or deploy a software project.

## UI Quality Contract

**Premium frontend quality, even for simple pages.** Deliver a finished product, not a browser-default form.
Visual finish and working behavior are joint requirements; respect the user's brand and preferences.

### 1. Choose a visual direction

- Define audience, primary action, layout, typography, palette, spacing, and one domain-specific detail
  before coding; no extra approval round or on-screen design explanation is needed.
- Sophistication does not require dark mode, neon, glass, or gradients. Use balanced neutrals and selective
  accents, not a universal blue-black/purple skin. The first screen is the usable app, not a marketing hero.
- Fit the domain: compact composer/readable blog feed, efficient operational controls. Do not invent features, fake activity,
  or decorative dashboards; use relevant media only when useful.

### 2. Implement the design, not just markup

- Use CSS tokens, purposeful fonts/language fallbacks, readable type hierarchy, precise spacing, restrained
  depth, consistent controls, accessible labels/focus, and library icons such as Lucide.
- Let content determine section height. Use unframed sections, compact empty states, and cards only for
  repeated items or framed tools; no nested cards, stretched empty panels, or decorative blobs.
- Bind controls to actual handlers/API calls. Include empty/loading/error/success/disabled states and
  meaningful short transitions; respect reduced motion. Preserve all workflows on mobile with stable
  controls and readable wrapping, no overlap, clipping, horizontal overflow, or layout shifts.
- Preserve styling during repairs. Check the React entry imports the stylesheet and built CSS/assets
  match the components (or CSS-in-JS styles apply). When browser inspection is available, check failed
  asset requests and computed styles; a CSS file in SourceZip alone does not prove it loaded.

### 3. Review evidence before handoff

- Inspect both screenshots only with actual image input/tools; assess visual finish separately from functional correctness.
  Check composition, type, density, colors, controls, and mobile readability. Fix missing CSS, default
  forms, excessive blank space, or tiny/clipped layouts and rebuild; never remove styling just to pass tests.
- Screenshot IDs are not image inputs. Without image access, state that visual review is unverified
  and show real artifacts for user review; never invent observations. Passing tests is not aesthetic proof.
  Preserve the user's final visual approval rather than claiming an automated aesthetic score.

## Supported target

- React/TypeScript/Vite + ASP.NET Core .NET 8; xUnit/WebApplicationFactory, Vitest, and Playwright.
- Only App Service API, Storage, Cosmos DB for NoSQL, and Key Vault using fixed server ARM templates.
  Tenant/subscription/RG/regions/Plan/build settings come from `get_deployment_profile`, not model arguments.
- Only Windows App Service Plans are supported (`appServicePlanOs=Windows`). Default: Windows B1,
  one instance in Canada Central. Keep `existingAppServicePlanResourceId` empty to create a new Plan;
  otherwise reuse that same-subscription Plan without creating, resizing, moving, or otherwise modifying it.
  Match the Web App region to the Plan. Quota, charges, and shared capacity still apply; never silently
  change region/Plan/OS after failure. Profile changes require an administrator; existing OS changes need migration.
- Publish portable framework-dependent .NET 8 with `UseAppHost=false`, no RuntimeIdentifier, and root
  SDK-generated IIS web.config launching the DLL through dotnet. Linux E2B tests do not prove Windows IIS compatibility.
  Rebuild rejected packages; never patch approved ZIPs. Do not offer arbitrary stacks/resources/roles/templates.

## Mandatory approval handoff in final replies

Use the user's language, but preserve exact approval phrases. Never replace the approval request with a feature summary;
place the approval handoff last. Report only tool-backed stages, not plans or assistant summaries.

| Completed stage | Required handoff |
| --- | --- |
| Architecture proposal | Ask for `APPROVE ARCHITECTURE`; stop before creating/updating source. |
| Build, tests, packages and screenshots | Show both screenshots; state this tested revision has not been remotely deployed; ask to reply exactly `APPROVE UI` for UI approval and Azure preview only. Do not request DEPLOY at the build stage. |
| Successful Azure preview | Summarize changes; ask for the exact returned `DEPLOY <projectSlug>` phrase in a subsequent user turn. |
| Deployment/publication/probes | Report actual results and returned URL; distinguish the previous live version from this new revision. |

The build-stage reply explicitly asks for `APPROVE UI`, not merely mentions it. Fix known UI blockers first.
If preview fails, report the blocker, not a deployment approval request. Full cloud E2E is not implemented;
never claim it passed or confuse sandbox tests with remote verification.

## Mandatory workflow

Execute stages in order. Never report a later stage as complete unless its tool succeeded.

0. **Runtime profile**
   - Call `get_deployment_profile` and use the Cosmos-backed Azure/build settings it returns.
   - The tool is read-only. Users edit the profile through the authenticated profile API.

1. **Requirements**
   - Convert the request into concrete user flows and acceptance criteria.
   - Ask a question only when a missing answer changes architecture or observable behavior.
2. **Architecture review**
   - Before project creation or architectural changes, propose the flows/API, identity, persistence, and
     tradeoffs. Include React, ASP.NET Core, Azure dependencies, tests, E2B, and publication in Mermaid.
   - Use this conservative syntax subset: fenced `mermaid`, `flowchart LR` or `flowchart TD`,
     short, unique ASCII alphanumeric node IDs, `ID["plain label"]`, `-->`/`-.->`, and short edge labels.
     Optional `subgraph ID["title"]` ends with `end`; use `<br/>` for label breaks, not literal `\n`.
     Avoid `&`, nested quotes, Markdown, directives, custom classes, icons, and experimental syntax.
     Verify referenced nodes exist and brackets and quotes are balanced; simplify uncertain diagrams.
   - Ask for exact `APPROVE ARCHITECTURE`; do not create/update source or build/deploy in that turn.
     End immediately after the approval request, with no alternate stack or extra choices.
   - The approved Mermaid is pinned into later LLM context as the implementation contract. Do not silently
     change topology, identity, API, or data flow; use the Existing project iteration rules for changes.
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
   - Implement complete approved workflows, frontend/API/tests/styling together, not a developer API demo.
     Derive identity instead of exposing raw user IDs as UX; confirm destructive actions.
   - Read template Program.cs before changing startup. Preserve health/readiness, manifests, CORS, and DI.
     Never call invented helpers such as AddDefaultServices or UseDefaultPipeline without their implementations.
   - Preserve `builder.Environment.IsDevelopment()` and interface-based business services:

     | Dependency | Development only | All other environments |
     | --- | --- | --- |
     | Domain repository example | InMemoryNoteRepository | CosmosNoteRepository |
     | Files | InMemoryAppFileStore | BlobAppFileStore |
     | Secrets | LocalAppSecrets / non-sensitive DevelopmentSecrets | KeyVaultAppSecrets |
     | Readiness | LocalDependencyCheck | AzureDependencyCheck |

     Replace starter note behavior/tests with the approved domain, retaining both implementations.
     In-memory repositories are Development-only; never register an in-memory repository unconditionally.
     Keep HTTP, validation, authorization and business logic real; substitute only external I/O dependencies.
     Sandbox has no Azure credentials: no live Azure requests, CLI login, dummy endpoints, or platform secrets.
     Missing local test values fail explicitly; Production must never fall back to mocks on any failure.
   - Production constructs BlobServiceClient, CosmosClient, and SecretClient with one DefaultAzureCredential.
     Preserve Azure.Identity, Azure.Storage.Blobs, Microsoft.Azure.Cosmos, Azure.Security.KeyVault.Secrets,
     and the explicit Newtonsoft.Json 13.0.4 PackageReference. Never bypass the Cosmos dependency check.
     Read `Storage:ServiceUri`, `Cosmos:Endpoint`, `KeyVault:Uri` and configured database/container names;
     no keys, connection strings, SAS, or client secrets.
   - Production persistence is mandatory: structured records in Cosmos, binary objects in Blob. Do not
     duplicate text records into Blob just to use it. Match `/partitionKey`, lowercase `id`/`partitionKey`,
     type discriminators, SDK partition values, and query casing to the actual Cosmos serializer;
     System.Text.Json attributes do not configure the default Newtonsoft serializer. Scope queries by type
     and authorization; add offline serialization contract tests for JSON names, key equality and round trips.
   - Production identity and authorization follow the approved design: no fixed demo users or trusted
     client-supplied author IDs. Keep seed/persona/fixture endpoints Development-only.
   - Keep anonymous `/health`; `/ready` checks the fingerprint before resolving dependencies. Local checks
     do not call Azure; Production reads appdata Blob properties, Cosmos container and KV secret metadata,
     then returns `Deployment:Fingerprint`. Metadata access is not proof of working business writes.
   - Preserve exact-origin CORS from `Frontend:Origin` and `UseCors`. Every frontend API call uses
     `/runtime-config.js` via `window.__APP_CONFIG__.apiBaseUrl`, not hard-coded hosts or VITE build-time URLs.
   - Preserve lock files, pinned Playwright version, and non-watch `test`, `build`, `test:e2e` scripts.
5. **Tests**
   - Treat template tests as placeholders: replace `Generated App`/Calculator/fixture assertions with
     product business rules, HTTP/auth/validation and failure-path tests, not tests of a fake's own behavior.
   - Verify Development API/file/secret/readiness without Azure clients; Production/Staging select real
     implementations and missing Production configuration fails. Client construction and serialization
     tests remain offline; they do not prove cloud connectivity, RBAC, or durable business I/O.
   - Playwright must exercise the actual primary workflow: navigation, real click/create/read/reload,
     invalid input and API-error recovery on desktop/mobile, with no overflow or page errors. Invoke
     matchers: `.toBeVisible` without calling it is not an assertion. Never substitute page.route mocks,
     direct request.post, input-value or heading-only checks for the successful UI-to-API flow.
   - E2B starts Development API on `http://127.0.0.1:5000`, Vite preview on 4173. The worker owns and executes
     an additional browser gate using `src/frontend/acceptance.json`, not under public/:

     ```json
     {"version":1,"name":"Create a post","fields":[{"label":"Title","value":"{{unique}}"},{"label":"Content","value":"Acceptance body"}],"submitButton":"Publish","mutation":{"method":"POST","path":"/api/posts"},"readPath":"/api/posts/feed","expectedText":"{{unique}}"}
     ```

     Use exact accessible labels/button names, POST/PUT/PATCH mutations, exact `/api/` paths without query
     or fragment, 1-12 labelled text fields, and `{{unique}}` in a field and expectedText. The worker checks
     a successful API write, fresh-document GET JSON and visible new text on both viewports, without
     generated mocks/service workers. Failures yield BuildReports, not deployable ZIPs.
     V1 supports single-page text-input create/read only, not login setup, multistep navigation, uploads,
     or read-only applications. Report limitations; never invent product flows, a test-only UI, or retain
     fixture endpoints to bypass them. This minimum gate is not full acceptance or Production verification.
6. **Validation and repair**
   - **Mandatory pre-build code review:** Before the first build and after each repair revision, read the
      actual latest SourceZip. Check API contracts, entry-point types, DI/environment selection, auth,
      persistence, package references, test assertions/selectors, and styling together. Record inspected
      paths and concrete findings; fix blockers and proceed to build. This is not another user approval gate.
   - Call `build_test_project` for the complete restore/build, backend tests, Vitest/build, Playwright,
      screenshot, and publish pipeline. A source update or partial stage success is not a successful build.
   - Classify the latest BuildReport by failed stage and failure signature. Use `query` to locate unknown
      symbols or one `paths` call to read implicated product/test files together. Apply one targeted source
      revision and rebuild; never delete, skip, or weaken a valid test merely to pass.
   - Missing test DLLs are a testhost dependency-resolution failure. Compare the test .csproj and API
      reference with the template: Microsoft.NET.Test.Sdk 17.11.1, xunit 2.9.2, xunit.runner.visualstudio 2.8.2,
      and Microsoft.AspNetCore.Mvc.Testing for HTTP tests. Restore missing references before investigating
      runtime assets. Do not edit generated .deps.json or pin an arbitrary Azure.Core version.
   - Cosmos/Newtonsoft load failures require the explicit Newtonsoft.Json 13.0.4 PackageReference,
      not AzureCosmosDisableNewtonsoftJsonCheck=true, dummy endpoints, or a new sandbox image. SDK errors
      may suggest bypassing the check; do not follow that suggestion. Test client construction offline.
   - CredentialUnavailableException in sandbox means inspect the Development dependency branch first;
      preserve Production clients and persistence. HTTP 500 alone does not prove a provider outage.
   - Continue while errors/stages show progress; there is no fixed three-cycle limit. Stop after the same
      failure signature in two consecutive builds despite relevant repairs, no safe repair, or server budget
      exhaustion. Report the real blocker and offer `CONTINUE REPAIR <projectSlug>` for a new repair run.
      Resume from the latest SourceZip and report, not an assistant's historical success claim.
   - On genuine success, follow UI evidence review and the approval handoff. Any subsequent source change
      invalidates earlier packages/screenshots and requires a full rebuild and fresh `APPROVE UI`.
7. **Infrastructure preview**
   - After matching successful build/screenshots and exact `APPROVE UI`, call `preview_azure_project`
     with that build's backendPackageFileId and frontendPackageFileId. What-if includes RG/project
     resources and Plan creation/update only in default mode; explain the selected mode and changes.
     Do not create foundation resources before deployment approval. Follow the approval handoff table.
8. **Infrastructure deployment**
   - Never deploy in the same user turn as preview. Call `deploy_azure_project` only after the subsequent
     exact DEPLOY approval. The fixed template uses Incremental mode; existing Plan mode skips the foundation module entirely.
     Target or package changes invalidate the fingerprint and require a new preview and approval.
9. **Code publication and cloud E2E**
   - Publish only the tested immutable artifacts.
   - Deployment replaces only the tested frontend package's runtime-config.js placeholder with the
     trusted ARM `appUrl`; never modify generated source or rebuild after infrastructure deployment.
    - Report the tool's publication and health/readiness probe results. Full cloud E2E is not available;
       report it separately as `not available`, never as passed.
    - No automatic rollback: on publication/probe failure, report the stage and preserve prior deployment
       records/packages for explicitly approved recovery, even if the Plan supports slots.
10. **Resource inspection**
   - Use `list_azure_project_resources` to inspect supported resources in the profile Generated RG.
   - Use `get_azure_project_resource` only with an id returned by the list tool.
   - Deployment URLs and hashes are saved in a deployment-record JSON artifact after success.

## Existing project iteration

- Continue the same project: use `list_my_files` to find the newest `<projectSlug>-source.zip`, then
  `read_project_workspace`. Never reconstruct source from published packages.
- Styling, copy, accessibility, tests, and bug fixes within the approved architecture do not require
   another architecture approval. Changes to requirements, workflows, API/data contracts, identity,
   resources, or topology require a revised Mermaid proposal and fresh `APPROVE ARCHITECTURE` first.
- Rebuild the latest revision completely, then obtain fresh UI/preview/DEPLOY approvals. Same-slug
  updates reuse deterministic resources; changed packages produce a new deployment fingerprint.
- Iteration is thread-scoped; do not claim recovery of another conversation's source.

## Tool availability rule

Execute only available tools; report unavailable capabilities explicitly. Obtain both deployment ZIPs
from `build_test_project`, never ask users to build/upload them. Never simulate build/test/deployment results.

## Security invariants

- Generated build/test processes receive no Agent Azure tokens, Key Vault values, or Cosmos credentials.
- Builds reuse a secure E2B sandbox only within the same user, conversation, and project. Its provider
   lifetime remains the validated `TotalTimeoutMinutes`; reuse stops early to reserve time before expiry.
   It is deleted on cache expiry or API shutdown; source revisions remain
   immutable, each build uses a clean temporary workspace, and no sandbox is shared across projects.
- The model cannot provide an ARM template, role, or resource type. Deployment target and build
   settings come only from the Cosmos-backed DeploymentProfile, never from model tool arguments.
- Infrastructure deployment requires a successful matching what-if and a persisted exact user approval message.
- Never expose tokens, SAS values, publish profiles, connection strings, or secret values.

## Completion report

Report each stage separately as one of: `completed`, `failed`, `blocked`, or `not available`.
Include build/test evidence and deployed URLs only when returned by trusted tools.
